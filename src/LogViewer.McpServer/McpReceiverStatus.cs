namespace LogViewer.McpServer
{
    /// <summary>
    /// Plain snapshot of one configured receiver, supplied by the WPF host (Settings + ReceiversViewModel).
    /// This project only references LogViewer.Core, so it cannot read Settings/Receiver (WPF) itself.
    /// </summary>
    public sealed class McpReceiverStatus
    {
        public string Name { get; set; }
        public int Port { get; set; }
        public string Transport { get; set; }
        public bool ConfiguredActive { get; set; }

        /// <summary>Whether sockets are currently started (global Start/Pause, not per-receiver).</summary>
        public bool IsReceiving { get; set; }
    }
}
