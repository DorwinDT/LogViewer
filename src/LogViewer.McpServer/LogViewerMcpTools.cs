using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogViewer.Core.Domain;
using LogViewer.Core.Services;

namespace LogViewer.McpServer
{
    /// <summary>
    /// MCP tool implementations over the live <see cref="LogSession"/> buffer. Read-only: no tool here
    /// changes receivers, filters, or the buffer. Each method returns a JSON string (not a typed object)
    /// so the result maps to a single TextContentBlock regardless of SDK structured-content nuances.
    /// </summary>
    public sealed class LogViewerMcpTools
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly LogProcessingService _processing;
        private readonly Func<IReadOnlyList<McpReceiverStatus>> _getReceiverStatus;

        public LogViewerMcpTools(LogProcessingService processing, Func<IReadOnlyList<McpReceiverStatus>> getReceiverStatus)
        {
            _processing = processing ?? throw new ArgumentNullException(nameof(processing));
            _getReceiverStatus = getReceiverStatus ?? throw new ArgumentNullException(nameof(getReceiverStatus));
        }

        [Description("Search the in-memory log buffer currently held by LogViewer. Returns the most recent matches first. " +
                     "search is a plain substring match (case-insensitive) against the message, logger, and address; " +
                     "level filters to that level and more severe (e.g. \"Warn\" also returns Error and Fatal).")]
        public string SearchLogs(
            [Description("Substring to search for in message/logger/address. Empty or omitted matches everything.")] string search = null,
            [Description("Minimum level: Trace, Debug, Info, Warn, Error, or Fatal. Omitted matches every level.")] string level = null,
            [Description("Maximum number of matches to return, newest first.")] int limit = 100)
        {
            if (limit <= 0) limit = 100;
            if (limit > 2000) limit = 2000;

            LogLevel? minLevel = ParseLevel(level);
            var all = _processing.Session.GetAllEntries();

            var matches = new List<LogEntry>();
            for (int i = all.Count - 1; i >= 0 && matches.Count < limit; i--)
            {
                var entry = all[i];
                if (minLevel.HasValue && !IsAtLeast(entry.Level, minLevel.Value))
                    continue;
                if (!string.IsNullOrEmpty(search) &&
                    !ContainsIgnoreCase(entry.Message, search) &&
                    !ContainsIgnoreCase(entry.Logger, search) &&
                    !ContainsIgnoreCase(entry.Address, search))
                    continue;
                matches.Add(entry);
            }

            var result = matches.Select(ToDto).ToList();
            return JsonSerializer.Serialize(result, JsonOptions);
        }

        [Description("Summary statistics over the entire in-memory log buffer: total entry count, counts by level, " +
                     "distinct logger count, and the time range of the oldest/newest entry.")]
        public string GetLogStatistics()
        {
            var all = _processing.Session.GetAllEntries();

            var countsByLevel = all
                .GroupBy(e => e.Level.ToString())
                .ToDictionary(g => g.Key, g => g.Count());

            var stats = new
            {
                totalCount = all.Count,
                countsByLevel,
                distinctLoggerCount = all.Select(e => e.FullPath).Distinct().Count(),
                oldestEntryTime = all.Count > 0 ? all.Min(e => e.Time) : (DateTime?)null,
                newestEntryTime = all.Count > 0 ? all.Max(e => e.Time) : (DateTime?)null
            };
            return JsonSerializer.Serialize(stats, JsonOptions);
        }

        [Description("Status of every receiver configured in LogViewer Settings (name, port, UDP/TCP, whether it is " +
                      "enabled in settings) and whether live receiving is currently started or paused.")]
        public string GetReceiverStatus()
        {
            var statuses = _getReceiverStatus();
            return JsonSerializer.Serialize(statuses, JsonOptions);
        }

        private static object ToDto(LogEntry e)
        {
            return new
            {
                time = e.Time,
                level = e.Level.ToString(),
                logger = e.Logger,
                address = e.Address,
                thread = e.Thread,
                message = e.Message,
                throwable = string.IsNullOrEmpty(e.Throwable) ? null : e.Throwable
            };
        }

        private static LogLevel? ParseLevel(string level)
        {
            if (string.IsNullOrWhiteSpace(level))
                return null;
            LogLevel parsed;
            return Enum.TryParse(level.Trim(), true, out parsed) ? parsed : (LogLevel?)null;
        }

        private static readonly Dictionary<LogLevel, int> SeverityRank = new Dictionary<LogLevel, int>
        {
            { LogLevel.Trace, 0 },
            { LogLevel.Debug, 1 },
            { LogLevel.Info, 2 },
            { LogLevel.Warn, 3 },
            { LogLevel.Error, 4 },
            { LogLevel.Fatal, 5 }
        };

        private static bool IsAtLeast(LogLevel entryLevel, LogLevel minLevel)
        {
            int entryRank, minRank;
            if (!SeverityRank.TryGetValue(entryLevel, out entryRank) || !SeverityRank.TryGetValue(minLevel, out minRank))
                return true;
            return entryRank >= minRank;
        }

        private static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
