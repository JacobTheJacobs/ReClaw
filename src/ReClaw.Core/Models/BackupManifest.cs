using System;
using System.Collections.Generic;

namespace ReClaw.Core.Models
{
    public class BackupManifest
    {
        public string SchemaVersion { get; set; } = "1";
        public DateTime CreatedAt { get; set; }
        public string Author { get; set; } = string.Empty;
        public List<PayloadEntry> Payload { get; set; } = new List<PayloadEntry>();
    }

    public class PayloadEntry
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
