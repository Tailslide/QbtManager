using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static QbtManager.qbtService;

namespace QbtManager
{
    public class ToDelete
    {
        public Torrent task {  get; set; }
        public DeleteMethod deleteMethod { get; set; }

        public ToDelete(Torrent task, DeleteMethod deleteFile) {
            this.task = task;
            this.deleteMethod = deleteFile;
        }
    }
}
