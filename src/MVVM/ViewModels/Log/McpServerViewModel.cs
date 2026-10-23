using System;
using System.Collections.Generic;
using System.Linq;
using LogViewer.Core.Services;
using LogViewer.McpServer;
using LogViewer.Services;
using NLog;

namespace LogViewer.MVVM.ViewModels.Log
{
    /// <summary>
    /// Owns the in-process MCP server's lifecycle: starts/stops/restarts it to match Settings
    /// (McpServerEnabled / McpServerPort), and supplies it with a live receiver-status snapshot.
    /// No UI of its own — Settings > General has the toggle and port field.
    /// </summary>
    public sealed class McpServerViewModel : IDisposable
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly IAppSettings _settings;
        private readonly ReceiversViewModel _receivers;
        private readonly LogViewerMcpHost _host;
        private int _runningPort = -1;

        public McpServerViewModel(LogProcessingService processing, IAppSettings settings, ReceiversViewModel receivers)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _receivers = receivers ?? throw new ArgumentNullException(nameof(receivers));
            _host = new LogViewerMcpHost(processing, BuildReceiverStatus,
                (e, message) => logger.Warn(e, $"MCP server: {message}"));
            ApplySettings();
        }

        /// <summary>Call after Settings are saved (or at startup) to match the current McpServerEnabled/Port.</summary>
        public void ApplySettings()
        {
            try
            {
                if (_settings.McpServerEnabled)
                {
                    if (_host.IsRunning && _runningPort != _settings.McpServerPort)
                    {
                        _host.StopAsync().GetAwaiter().GetResult();
                        _runningPort = -1;
                    }
                    if (!_host.IsRunning)
                    {
                        _host.Start(_settings.McpServerPort);
                        _runningPort = _settings.McpServerPort;
                        logger.Debug($"MCP server started on port {_runningPort}");
                    }
                }
                else if (_host.IsRunning)
                {
                    _host.StopAsync().GetAwaiter().GetResult();
                    _runningPort = -1;
                    logger.Debug("MCP server stopped");
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "An error occurred while starting/stopping the MCP server.");
            }
        }

        private IReadOnlyList<McpReceiverStatus> BuildReceiverStatus()
        {
            bool isReceiving = !_receivers.StartIsEnabled;
            return _receivers.Receivers.Select(r => new McpReceiverStatus
            {
                Name = r.Name,
                Port = r.Port,
                Transport = r.Transport.ToString(),
                ConfiguredActive = r.IsActive,
                IsReceiving = isReceiving
            }).ToList();
        }

        public void Dispose()
        {
            try
            {
                if (_host.IsRunning)
                    _host.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                logger.Warn(e, "An error occurred while stopping the MCP server.");
            }
        }
    }
}
