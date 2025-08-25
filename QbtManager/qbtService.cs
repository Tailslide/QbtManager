using System;
using System.Collections.Generic;
using RestSharp;
using System.Net;
using System.IO;
using System.Text.Json;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text;
using System.Security.Cryptography;

namespace QbtManager
{
    /// <summary>
    /// Wrapper for the QBitorrent service.
    /// </summary>
    public class qbtService
    {
        private readonly RestClient client;
        private readonly QBittorrentSettings settings;
        private Version? qbtVersion;

        public class Tracker
        {
            public string url { get; set; }
            public int status { get; set; }
            public string msg { get; set; }
        }

        public class Torrent 
        {
            public string hash { get; set; }  
            public string category { get; set; }  
            public string name { get; set; }
            public string tracker { get; set; }
            public string magnet_uri { get; set; }
            public string state { get; set; }
            public int up_limit { get; set; }
            public float max_ratio { get; set; }
            public int max_seeding_time { get; set; }
            public DateTime added_on { get; set; }
            public DateTime completed_on { get; set; }
            public List<Tracker> trackers { get; set; }
            public string tags { get; set; }

            public override string ToString()
			{
                var age = DateTime.Now - added_on;
                string span = "(" + state + ", " + age.ToHumanReadableString() + ")";
                return $" * {name} {span}";
			}
		}

        public qbtService(QBittorrentSettings qbtSettings)
        {
            settings = qbtSettings;

            client = new RestClient(settings.url)
            {
                CookieContainer = new CookieContainer()
            };
            //var options = new RestClientOptions
            //{
            //    CookieContainer = new CookieContainer(),
            //    BaseUrl = new Uri( settings.url )
            //};
            //client = new RestClient(options);
        }

        /// <summary>
        /// Authenticate and get an SID token
        /// </summary>
        /// <returns></returns>
        public bool SignIn()
        {
            try
            {
                CookieContainer cookies = new CookieContainer();
                Uri url = new Uri(settings.url + "/auth/login");

                var request = new RestRequest("/auth/login", Method.POST);
                request.AddParameter("Referer", settings.url);  // url to the page I want to go

                if (string.IsNullOrEmpty(settings.password))
                {
                    Utils.Log("No password specified - assuming local auth is disabled in QBT");
                }
                else
                {
                    request.AddParameter("username", settings.username);
                    request.AddParameter("password", settings.password);
                }

                var response = client.Execute(request);

                var sessionCookie = response.Cookies.SingleOrDefault(x => x.Name == "SID");
                if (sessionCookie != null)
                {
                    client.CookieContainer.Add(new Cookie(sessionCookie.Name, sessionCookie.Value, sessionCookie.Path, sessionCookie.Domain));
                    return true;
                }
            }
            catch( Exception ex )
            {
                Utils.Log("Exception! " + ex.Message);
            }

            return false;
        }

        public void GetQBTVersion()
        {
            // Dont use ?filter=completed here - we'll filter ourselves.
            var versionStr = MakeRestRequest("/app/version", null);

            if (!string.IsNullOrEmpty(versionStr))
            {
                if( versionStr.StartsWith("v") )
                    versionStr = versionStr.Substring(1);
                
                qbtVersion = new Version(versionStr);
                Utils.Log( $"QBT Version is: {qbtVersion}");
            }
        }
        
        /// <summary>
        /// Get the list of torrents
        /// </summary>
        /// <returns></returns>
        public IList<Torrent> GetTasks()
        {
            var parms = new Dictionary<string, string>();

            // Dont use ?filter=completed here - we'll filter ourselves.
            var data = MakeRestRequest<List<Torrent>>("/torrents/info", parms);

            if (data != null)
            {
                foreach (var torrent in data)
                {
                    var torrentId = new Dictionary<string, string> { { "hash", torrent.hash } };

                    var track = MakeRestRequest<List<Tracker>>("/torrents/trackers", torrentId);

                    torrent.trackers = track;
                }
            }

            return data;
        }

        public IList<TorrentFile> GetTorrentFiles(string hash)
        {
            // Assuming a File class that represents a file within a torrent
            var files = MakeRestRequest<List<TorrentFile>>($"/torrents/files?hash={hash}", new Dictionary<string, string>());

            return files ?? new List<TorrentFile>();
        }

        /// <summary>
        /// Delete a list of torrents via hash
        /// </summary>
        /// <param name="taskIds">Id of task</param>
        /// <param name="deletefile">If true also delete the file on disk</param>
        /// <returns></returns>
        public bool DeleteTask( IEnumerable<ToDeleteHashes> toDeletes)
        {
            var res = true;
            var parms = new Dictionary<string, string>();

            var deleteAlls = toDeletes.Where(x => x.deletemethod == DeleteMethod.DeleteFileAndTask);
            var deleteTaskOnlys = toDeletes.Where(x => x.deletemethod == DeleteMethod.DeleteTask);

            if (deleteAlls.Any()) {
                parms["hashes"] = string.Join("|", deleteAlls.Select(x=>x.hash));
                parms["deleteFiles"] = "true";
                res = ExecuteCommand("/torrents/delete", parms);
            }
            if (deleteTaskOnlys.Any())
            {
                parms["hashes"] = string.Join("|", deleteTaskOnlys.Select(x => x.hash));
                parms["deleteFiles"] = "false";
                res &= ExecuteCommand("/torrents/delete", parms);
            }
            return res;
        }

        /// <summary>
        /// Download a torrent, given a URL, adding an optional category
        /// </summary>
        /// <param name="torrentUrl"></param>
        /// <param name="category"></param>
        /// <returns></returns>
        public bool DownloadTorrent(string torrentUrl, string category)
        {
            var parms = new Dictionary<string, string>();

            parms["urls"] = torrentUrl;
            parms["category"] = category;
            return ExecuteCommand("/torrents/add", parms );
        }

        /// <summary>
        /// Pause a list of torrents, via hashes
        /// </summary>
        /// <param name="taskIds"></param>
        /// <returns></returns>
        public bool PauseTask(string[] taskIds)
        {
            // Handle the fact that pause => stop in QBT v5
            var command = qbtVersion != null && qbtVersion.Major < 5 ? "pause" : "stop";
            var parms = new Dictionary<string, string>();

            foreach (var chunk in taskIds.Chunk(30))
            {
                parms["hashes"] = string.Join("|", chunk);
                if (!ExecuteCommand($"/torrents/{command}", parms))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Sets the upload limit for a list of hashes
        /// </summary>
        /// <param name="taskIds"></param>
        /// <param name="limitKiloBytesPerSec">Upload limit in KB/s</param>
        /// <returns></returns>
        public bool SetUploadLimit(string[] taskIds, int limitKiloBytesPerSec)
        {
            var parms = new Dictionary<string, string>();

            // Convert from KB to bytes, for the service
            int limitBytesPerSec = limitKiloBytesPerSec * 1024;

            parms["hashes"] = string.Join("|", taskIds);
            parms["limit"] = limitBytesPerSec.ToString();

            return ExecuteCommand("/torrents/setUploadLimit", parms);
        }

        /// <summary>
        /// Sets the maximum ratio and seeding time for a list of hashes
        /// </summary>
        /// <param name="taskIds"></param>
        /// <param name="maxRatio">Maximum Ratio, -2 = none, -1 = use global, other value = custom ratio per torrent</param>
        /// <param name="maxSeedingTime">Maximum Seeding Time, -2 = none, -1 = use global, other value = minutes to seed this torrent</param>
        /// <returns></returns>
        public bool SetMaxLimits(string[] taskIds, float maxRatio, int maxSeedingTime)
        {
            var parms = new Dictionary<string, string>();

            parms["hashes"] = string.Join("|", taskIds);
            parms["ratioLimit"] = maxRatio.ToString();
            parms["seedingTimeLimit"] = maxSeedingTime.ToString();
            parms["inactiveSeedingTimeLimit"] = "-1"; // new parameter https://github.com/qbittorrent/qBittorrent/pull/19294
            Utils.Log("Setting Limits to ratio " + parms["ratioLimit"] + " seeding time " + parms["seedingTimeLimit"] + " for " + taskIds.Length.ToString() + " tasks ");
            return ExecuteCommand("/torrents/setShareLimits", parms);
        }

        /// <summary>
        /// Execute a GET request, passing the Auth token
        /// </summary>
        /// <param name="requestMethod"></param>
        /// <param name="parms"></param>
        /// <returns></returns>
        public bool ExecuteRequest( string requestMethod, IDictionary<string, string> parms )
        {
            var request = new RestRequest(requestMethod, Method.GET);

            foreach (var kvp in parms)
                request.AddParameter(kvp.Key, kvp.Value);

            var queryResult = client.Execute(request);

            if (queryResult.StatusCode != HttpStatusCode.OK)
            {
                Utils.Log($"ERROR: Request failed: {requestMethod}: {queryResult.ResponseStatus}");
            }

            return queryResult.StatusCode == HttpStatusCode.OK;
        }

        /// <summary>
        /// Execute a POST request, passing the Auth token
        /// </summary>
        /// <param name="requestMethod"></param>
        /// <param name="parms"></param>
        /// <returns></returns>
        public bool ExecuteCommand(string requestMethod, IDictionary<string, string> parms)
        {
            var request = new RestRequest(requestMethod, Method.POST);
            
            foreach (var kvp in parms)
                request.AddParameter(kvp.Key, kvp.Value, ParameterType.GetOrPost);

            var queryResult = client.Execute(request);

            if( queryResult.StatusCode != HttpStatusCode.OK )
            {
                Utils.Log($"ERROR: Command failed: {requestMethod}: {queryResult.ResponseStatus}");
            }

            return queryResult.StatusCode == HttpStatusCode.OK;
        }

        
        /// <summary>
        /// Generic REST method handler.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="requestMethod"></param>
        /// <param name="parms"></param>
        /// <param name="method"></param>
        /// <returns></returns>
        public string MakeRestRequest(string requestMethod, IDictionary<string, string>? parms)
        {
            var request = new RestRequest(requestMethod, Method.GET );

            if (parms != null)
            {
                foreach (var kvp in parms)
                    request.AddParameter(kvp.Key, kvp.Value, ParameterType.GetOrPost);
            }
            
            try
            {
                var queryResult = client.Execute<string>(request);

                if (queryResult != null)
                {
                    if (queryResult.StatusCode != HttpStatusCode.OK)
                    {
                        Utils.Log("Error: {0} - {1}", queryResult.StatusCode, queryResult.Content);
                    }
                    else
                    {
                        return queryResult.Content;
                    }
                }
                else
                    Utils.Log("No valid queryResult.");
            }
            catch (Exception ex)
            {
                Utils.Log("Exception: {0}: {1}", ex.Message, ex);
            }

            return string.Empty;
        }

        /// <summary>
        /// Generic REST method handler.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="requestMethod"></param>
        /// <param name="parms"></param>
        /// <param name="method"></param>
        /// <returns></returns>
        public T MakeRestRequest<T>(string requestMethod, IDictionary<string, string>? parms, Method method = Method.GET)
        {
            var request = new RestRequest(requestMethod, method );

            if (parms != null)
            {
                foreach (var kvp in parms)
                    request.AddParameter(kvp.Key, kvp.Value, ParameterType.GetOrPost);
            }
            
            try
            {
                var queryResult = client.Execute<T>(request);

                if (queryResult != null)
                {
                    if (queryResult.StatusCode != HttpStatusCode.OK)
                    {
                        Utils.Log("Error: {0} - {1}", queryResult.StatusCode, queryResult.Content);
                    }
                    else
                    {
                        JsonSerializerOptions options = new JsonSerializerOptions();
                        options.Converters.Add(new UnixToNullableDateTimeConverter());
                        T response = JsonSerializer.Deserialize<T>(queryResult.Content, options);

                        if (response != null)
                        {
                            return response;
                        }
                        else
                            Utils.Log("No response Data.");
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.Log("Exception: {0}: {1}", ex.Message, ex);
            }

            return default(T);
        }



        public string GenerateTorrentFileHash(string torrentHash)
        {
            // Fetch the files for the given torrent
            IList<TorrentFile> files = GetTorrentFiles(torrentHash);

            // Combine file names and sizes into a single string
            StringBuilder combined = new StringBuilder();
            foreach (var file in files.OrderBy(f => f.Name))
            {
                combined.Append($"{file.Name}{file.Size}");
            }

            // Convert the combined string to bytes
            byte[] combinedBytes = Encoding.UTF8.GetBytes(combined.ToString());

            // Compute the SHA256 hash of this byte array
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(combinedBytes);

                // Convert the byte array to a hex string
                StringBuilder hash = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    hash.Append(b.ToString("x2"));
                }
                if (hash.ToString() =="5feceb66ffc86f38d952786c6d696c79c2dbc239dd4e91b46729d73a27fb57e9")
                {
                    Debug.WriteLine("hash found");
                }
                return hash.ToString();
            }
        }
        public class TorrentFile
        {
            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("size")]
            public long Size { get; set; }

            public TorrentFile(string name, long size)
            {
                Name = name;
                Size = size;
            }
        }

        public class UnixToNullableDateTimeConverter : JsonConverter<DateTime>
        {
            public override bool HandleNull => true;
            public bool? IsFormatInSeconds { get; set; } = null;

            public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TryGetInt64(out var time))
                {
                    // if 'IsFormatInSeconds' is unspecified, then deduce the correct type based on whether it can be represented in the allowed .net DateTime range
                    if (IsFormatInSeconds == true || IsFormatInSeconds == null && time > _unixMinSeconds && time < _unixMaxSeconds)
                        return DateTimeOffset.FromUnixTimeSeconds(time).LocalDateTime;
                    return DateTimeOffset.FromUnixTimeMilliseconds(time).LocalDateTime;
                }

                return DateTime.MinValue;
            }

            public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => throw new NotSupportedException();

            private static readonly long _unixMinSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds() - DateTimeOffset.UnixEpoch.ToUnixTimeSeconds(); // -62_135_596_800
            private static readonly long _unixMaxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds() - DateTimeOffset.UnixEpoch.ToUnixTimeSeconds(); // 253_402_300_799
        }
    }
}
