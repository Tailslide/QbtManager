using QBTCleanup;
using QBTManager.Logging;
using System.Reflection;
using System.ServiceModel.Syndication;
using System.Xml;
using static QbtManager.qbtService;


namespace QbtManager
{
    public class MainClass
    {
        private static Dictionary<Torrent, string> tasksFileHashes = new Dictionary<Torrent, string>();
        /// <summary>
        /// Given a task, see which tracker in the settings matches it.
        /// </summary>
        /// <param name="task"></param>
        /// <param name="settings"></param>
        /// <returns></returns>
        protected static Tracker FindTaskTracker(Torrent task, Settings settings)
        {
            Tracker tracker = null;

            if (settings.trackers == null || !settings.trackers.Any())
                return null;

            if (tracker == null)
                tracker = settings.trackers.FirstOrDefault(x => task.magnet_uri.ToLower().Contains(x.tracker));

            if (tracker == null)
                tracker = settings.trackers.FirstOrDefault(t => task.tracker.ToLower().Contains(t.tracker));

            // Allow wildcard ("keep all trackers")
            if( tracker == null )
                tracker = settings.trackers.FirstOrDefault(x => x.tracker == "*");

            return tracker;
        }

        private static readonly List<string> downloadedStates = new List<string> { "uploading", "pausedUP", "queuedUP", "stalledUP", "checkingUP", "forcedUP" };

        protected static bool IsDeletable( Torrent task, Tracker tracker, out string reason )
        {
            bool canDelete = false;
            reason = "";
            if (downloadedStates.Contains(task.state))
            {
                canDelete = true;

                if (tracker != null)
                {
                    // If the torrent is > 90 days old, delete
                    var age = DateTime.Now - task.added_on;

                    if (tracker.maxDaysToKeep == -1 || age.TotalDays < tracker.maxDaysToKeep)
                        canDelete = false;
                    else
                        reason = "too old";
                }
                else
                    reason = "wrong tracker";
                        //Utils.Log("Task {0} deleted - wrong tracker", task.name);

            }

            return canDelete;
        }



        public static void Main(string[] args)
        {
            var settingPath = args.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

            LogHandler.InitLogs();
            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            Utils.Log($"QbtManager Version: {assemblyVersion}");
            
            if (string.IsNullOrEmpty(settingPath))
                settingPath = "Settings.json";

            try
            {
                if (!File.Exists(settingPath))
                {
                    Utils.Log("Settings not found: {0}", settingPath);
                    return;
                }

                string json = File.ReadAllText(settingPath);

                var settings = Utils.deserializeJSON<Settings>(json);

                qbtService service = new qbtService(settings.qbt);

                if (settings.deleteTasks)
                {
                    if (settings.deleteFiles)
                        Utils.Log("Filtered torrents will be deleted with their content.");
                    else
                        Utils.Log("Filtered torrents will be deleted (files will not be deleted).");
                }
                else
                    Utils.Log("Filtered torrents will be paused.");

                Utils.Log("Signing in to QBittorrent.");

                if (service.SignIn())
                {
                    Utils.Log("Getting Seeding Task list and mapping trackers...");
                    var tasks = service.GetTasks()
                                       .OrderBy(x => x.name)
                                       .ToList();

                    if (settings.delete_task_not_file_if_other_tasks)
                    {
                        Utils.Log("Creating hashes for all torrent file sizes and names");
                        foreach (var t in tasks)
                        {
                            if (t != null)
                            {
                                string filehash = service.GenerateTorrentFileHash(t.hash);
                                tasksFileHashes[t] = filehash;
                            }
                        }
                    }

                    ProcessTorrents(service, tasks, settings);

                    if( settings.rssfeeds != null )
                        ReadRSSFeeds(service, settings.rssfeeds);
                }
                else
                    Utils.Log("Login failed.");
            }
            catch (Exception ex)
            {
                Utils.Log("Error initialising. {0}", ex);
            }
        }

        private static bool TrackerMsgIsDeletable( Torrent task, Tracker trackerSettings )
        {
            if (task.trackers == null || trackerSettings.deleteMessages == null )
                return false;

            var filterMsgs = trackerSettings.deleteMessages;

            var torrentMsgs = task.trackers.Where(x => !String.IsNullOrEmpty(x.msg)).Select(x => x.msg);

            if (torrentMsgs.Any(x => filterMsgs.Contains(x, StringComparer.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        private static bool IsDeleteTaskOnlyCategory(Torrent task, Settings settings, out string reason)
        {
            reason = "";
            if (settings.delete_task_not_file_categories.Any(x => x.ToUpper() == task.category.ToUpper()))
            {
                reason = $"Category={task.category}";
                //Utils.Log($" - Only deleting Task not File with Category {task.category} : {task}");
                return true;
            }
            else
                return false;
        }

        private static bool IsDeleteTaskOnlyTag(Torrent task, Settings settings, out string reason)
        {
            reason = "";
            var tags = task.tags.Split(',');
            var matching = settings.delete_task_not_file_tags.FirstOrDefault(x => tags.Any(y => y.ToUpper() == x.ToUpper()));
            if (matching != null)
            {
                reason = $"Tags Contains {matching}";
                Utils.Log($" - Only deleting Task not File with Tag {matching} : {task}");
                return true;
            }
            else
                return false;
        }

        private static void ProcessTorrents(qbtService service, IList<Torrent> tasks, Settings settings )
        {
            Utils.Log("Processing torrent list...");

            var toKeep = new List<Torrent>();
            var toDelete = new List<ToDelete>();
            var limits = new Dictionary<Torrent, int>();
            var maxLimits = new Dictionary<Torrent, (float,int)>(); // max ratio, seeding time. API requires they both get set at once

            foreach (var task in tasks)
            {
                bool keepTask = true;
                var tracker = FindTaskTracker(task, settings);
                string deleteReason = "";
                if (tracker != null)
                {
                    if (IsDeletable(task, tracker, out deleteReason))
                        keepTask = false;

                    if (TrackerMsgIsDeletable(task, tracker))
                    {
                        deleteReason += $" torrent contains delete message for tracker";
                        keepTask = false;
                    }
                }

                if (keepTask)
                {
                    toKeep.Add(task);
                    Utils.Log($" - Keep: {task}");

                    if (tracker != null && tracker.up_limit.HasValue && task.up_limit != tracker.up_limit)
                    {
                        // Store the tracker limits.
                        limits[task] = tracker.up_limit.Value;
                    }

                    if (tracker != null && tracker.max_ratio.HasValue && task.max_ratio != tracker.max_ratio)
                    {
                        // Store the tracker limits.
                        maxLimits[task] = (tracker.max_ratio.Value, task.max_seeding_time);
                        // API call can't set just one have to set both
                    }

                    if (tracker != null && tracker.max_seeding_time.HasValue && task.max_seeding_time != tracker.max_seeding_time)
                    {
                        // Store the tracker limits.
                        // API call can't set just one have to set both
                        if (maxLimits.ContainsKey(task))
                        {
                            maxLimits[task] = (maxLimits[task].Item1, tracker.max_seeding_time.Value);
                        }
                        else
                        {
                            maxLimits[task] = (task.max_ratio, tracker.max_seeding_time.Value);
                        }

                    }
                }
                else
                {
                    if (!settings.deleteTasks && task.state.StartsWith("pause"))
                    {
                        Utils.Log($" - Already paused: {task} Reason:{deleteReason}");
                        // Nothing to do, so skip.
                        continue;
                    }
                    if (!settings.deleteTasks)
                    {
                        Utils.Log($" - Pause: {task} Reason:{deleteReason}");
                        toDelete.Add(new ToDelete(task, DeleteMethod.PauseTask));
                    }
                    if (!settings.deleteFiles)
                    {
                        Utils.Log($" - Delete Task Only: {task} Reason:{deleteReason}");
                        toDelete.Add(new ToDelete(task, DeleteMethod.DeleteTask));
                    }
                    else
                    {
                        string deleteTaskOnlyReason = "";
                        bool deleteTaskOnly = (IsDeleteTaskOnlyCategory(task, settings, out deleteTaskOnlyReason) || IsDeleteTaskOnlyTag(task, settings, out deleteTaskOnlyReason));
                        if (deleteTaskOnly)
                        {
                            Utils.Log($" - Delete Task Only: {task} Reason:{deleteReason} Don't Delete Files Reason:{deleteTaskOnlyReason}");
                            toDelete.Add(new ToDelete(task, DeleteMethod.DeleteTask));
                        }
                        else if (settings.delete_task_not_file_if_other_tasks && tasks.Count(x => x.hash == task.hash) > 1)
                        {
                            Utils.Log($" - Delete Task Only: {task} Reason:{deleteReason} Don't Delete Files Reason: Another torrent has same hash");
                            toDelete.Add(new ToDelete(task, DeleteMethod.DeleteTask));

                        }
                        else if (settings.delete_task_not_file_if_other_tasks && tasksFileHashes.ContainsKey(task) && tasksFileHashes.Count(x => x.Value == tasksFileHashes[task]) > 1)
                        {
                            var otherTasksWithSameFiles = tasksFileHashes.Where(x => x.Value == tasksFileHashes[task] && x.Key != task);
                            Torrent firstTask = otherTasksWithSameFiles.First().Key;

                            if (toDelete.Any(x => tasksFileHashes[x.task] == tasksFileHashes[task]))
                            {
                                Utils.Log($" - Skipping delete of: {task} Reason: a task with the same files and sizes is already marked for removal");
                                toDelete.Add(new ToDelete(task, DeleteMethod.DeleteTask));

                            }
                            else
                            {
                                Utils.Log($" - Delete Task Only: {task} Reason:{deleteReason} Don't Delete Files Reason: Another torrent uses the same files: {firstTask.name}");
                                toDelete.Add(new ToDelete(task, DeleteMethod.DeleteTask));
                            }
                        }
                        else
                        {
                            Utils.Log($" - Delete Task and File: {task} Reason:{deleteReason} {deleteTaskOnlyReason}");
                            toDelete.Add(new ToDelete(task, DeleteMethod.DeleteFileAndTask));
                        }
                    }
                }
            }

            if (limits.Any())
            {
                var limitGroups = limits.GroupBy(x => x.Value, y => y.Key);

                foreach (var x in limitGroups)
                {
                    int limit = x.Key;
                    var hashes = x.Select(t => t.hash).ToArray();
                    if (!service.SetUploadLimit(hashes, limit))
                        Utils.Log($"Failed to set upload limits.");
                }
            }

            if (maxLimits.Any())
            {
                var ratioGroups = maxLimits.GroupBy(x => x.Value, y => y.Key);

                foreach (var x in ratioGroups)
                {
                    float ratio_limit = x.Key.Item1;
                    int time_limit = x.Key.Item2;

                    var hashes = x.Select(t => t.hash).ToArray();
                    if (!service.SetMaxLimits(hashes, ratio_limit, time_limit))
                        Utils.Log($"Failed to set max ratio and time limit.");
                }
            }

            if (toDelete.Any())
            {
                var hashes = toDelete.Select(x => new ToDeleteHashes() {  hash= x.task.hash, deletemethod= x.deleteMethod})
                                           .Distinct()
                                           .ToArray();
                var deleteHashes = hashes.Where(x => x.deletemethod == DeleteMethod.DeleteFile || x.deletemethod == DeleteMethod.DeleteTask || x.deletemethod== DeleteMethod.DeleteFileAndTask);
                var pauseHashes = hashes.Where(x => x.deletemethod == DeleteMethod.PauseTask);

                if (settings.deleteTasks && deleteHashes.Any())
                {
                    Utils.Log($"Deleting {deleteHashes.Count()} tasks...");
                    service.DeleteTask(deleteHashes);
                }

                if (pauseHashes.Any())
                {
                    Utils.Log($"Pausing {pauseHashes.Count()} tasks...");
                    service.PauseTask(pauseHashes.Select(x=>x.hash).ToArray());
                }

                if (settings.email != null)
                {
                    Utils.Log("Sending alert email.");
                    Utils.SendAlertEmail(settings.email, toDelete.Select(x=>x.task));
                }
            }
            else
                Utils.Log("No tasks to delete/pause.");
        }

        private static void ReadRSSFeeds(qbtService service, List<RSSSettings> settings)
        {
            if( settings != null && settings.Any() )
            { 
                Utils.Log("Processing RSS feed list...");

                foreach (var rssFeed in settings)
                {
                    ReadRSSFeed(service, rssFeed);
                }
            }
            else
                Utils.Log("No RSS feeds to process...");
        }

        private static void ReadRSSFeed(qbtService service, RSSSettings rssFeed)
        {
            Utils.Log("Reading RSS feed for {0}", rssFeed.url);

            try
            {
                XmlReader reader = XmlReader.Create(rssFeed.url);
                SyndicationFeed feed = SyndicationFeed.Load(reader);
                reader.Close();

                if (feed.Items.Any())
                {
                    // Cache list and save dates. Filter by list entry and oldest date > 1 month
                    DownloadItems(service, feed.Items);
                }
                else
                    Utils.Log("No RSS items found.");
            }
            catch (Exception ex)
            {
                Utils.Log("Error reading feed: {0}", ex.Message);
            }
        }

        private static void DownloadItems(qbtService service, IEnumerable<SyndicationItem> items)
        {
            Utils.Log("Processing {0} RSS feed items.", items.Count() );

            DownloadHistory history = new DownloadHistory();

            history.ReadHistory();

            foreach (SyndicationItem item in items)
            {
                string subject = item.Title.Text;

                var link = item.Links.FirstOrDefault();

                if (link != null)
                {
                    string torrentUrl = link.Uri.ToString();

                    if( history.Contains( torrentUrl ) )
                    {
                        Utils.Log("Skipping item for {0} (downloaded already).", subject, torrentUrl);
                        continue;
                    }

                    Utils.Log("Sending link for {0} ({1}) to QBT...", subject, torrentUrl);

                    if (service.DownloadTorrent(torrentUrl, "freeleech"))
                    {
                        history.AddHistory(item);
                    }
                    else
                        Utils.Log("Error: torrent add failed.");
                }
            }

            history.WriteHistory();
        }
    }
}
