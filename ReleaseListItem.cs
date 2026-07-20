using System.ComponentModel;
using System.Runtime.CompilerServices;
using BE_Cruncher.Models;

namespace BE_Cruncher;

public sealed class ReleaseListItem : INotifyPropertyChanged
{
    public GitHubRelease Release { get; }

    public string DisplayName => Release.DisplayName;
    public string Published => Release.PublishedAt?.LocalDateTime.ToString("yyyy-MM-dd") ?? "-";
    public bool Prerelease => Release.Prerelease;

    private string _status;
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public ReleaseListItem(GitHubRelease release, string status)
    {
        Release = release;
        _status = status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
