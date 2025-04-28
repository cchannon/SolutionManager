using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

namespace SolutionManager.Models
{
    public class SolutionProfile : INotifyPropertyChanged
    {
        private bool _isFavorite { get; set; } = false;

        public string FriendlyName { get; set; } = "";
        public string UniqueName { get; set; } = "";
        public string Version { get; set; } = "";
        public bool IsManaged { get; set; } = false;

        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    OnPropertyChanged(nameof(IsFavorite));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class GuidConfig
    {
        public required string GUID { get; set; }
        public required string NewValue { get; set; }
        public required string TargetEnvironment { get; set; }
    }

    public class AuthProfile
    {
        public int Index { get; set; }
        public bool Active { get; set; }
        public required string Kind { get; set; }
        public required string Name { get; set; }
        public required string User { get; set; }
        public string? Cloud { get; set; }
        public string? Type { get; set; }
        public required string Environment { get; set; }
        public required string EnvironmentUrl { get; set; }
        public string DisplayName => $"{Name} ({Environment})";
    }

    public class EnvironmentProfile
    {
        public required string DisplayName { get; set; }
        public required string EnvironmentId { get; set; }
        public required string EnvironmentUrl { get; set; }
        public required string UniqueName { get; set; }
        public bool Active { get; set; }
    }

    public class RunningJob
    {
        public string Id { get; } = Guid.NewGuid().ToString(); // Unique ID for the job
        public required string Name { get; set; }
        public required string Status { get; set; }
        public string? Environment { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
        public required DateTime Timestamp { get; set; }
        public string DisplayName => $"{(Status == "In Progress" ? "🏃‍♂️" : Status == "Failed" ? "🤬" : Status == "Waiting" ? "⏳" : Status == "Cancelled" ? "❌" : "🥳")} {Name}";
        public string? PredecessorId { get; set; } // The ID of the predecessor job
        public Func<RunningJob, Task>? JobLogic { get; set; } // The logic to be executed for the job
    }

    public class EVSettings
    {
        public required string SchemaName { get; set; }
        public required string Value { get; set; }
        public required string Type { get; set; }
        public required string TargetEnvironment { get; set; }
    }

    public class CRSettings
    {
        public required string LogicalName { get; set; }
        public required string ConnectionId { get; set; }
        public required string ConnectorId { get; set; }
        public required string TargetEnvironment { get; set; }
    }

    public class GlobalDiscoveryResponse
    {
        public List<Instance> value { get; set; }
    }

    public class Instance
    {
        public string FriendlyName { get; set; }
        public string EnvironmentId { get; set; }
        public string ApiUrl { get; set; }
        public string UniqueName { get; set; }
        public string State { get; set; }
    }
}