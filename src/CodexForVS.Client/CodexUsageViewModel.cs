using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexForVS.Client;

// State changes run on the UI dispatcher. A read can overlap a push notification or a new sign-in.
public sealed class CodexUsageViewModel : INotifyPropertyChanged
{
    private readonly ICodexAppServerClient client;
    private readonly IUiDispatcher dispatcher;
    private CancellationTokenSource? session;
    private int revision;
    private bool reading;
    private bool refreshFailed;
    private DateTimeOffset? updatedAt;
    private bool? ordinaryUsageAllowed;
    private bool rateLimitReached;

    internal CodexUsageViewModel(ICodexAppServerClient client, IUiDispatcher dispatcher)
    {
        this.client = client;
        this.dispatcher = dispatcher;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => session != null && !reading);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public AsyncCommand RefreshCommand { get; }
    public IReadOnlyList<CodexUsageWindow> Windows { get; private set; } = Array.Empty<CodexUsageWindow>();
    public string Summary => ordinaryUsageAllowed == false || rateLimitReached ? "Included usage limit reached" :
        Windows.Count > 0 ? "Usage · " + Windows.OrderBy(window => window.PercentRemaining).First().Description :
        reading ? "Checking usage…" : "Usage unavailable";
    public string Status
    {
        get
        {
            string lastUpdate = updatedAt.HasValue ? "Updated " + updatedAt.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) : string.Empty;
            if (reading) return "Refreshing… " + lastUpdate;
            if (refreshFailed && updatedAt.HasValue) return "Refresh failed. " + lastUpdate;
            return updatedAt.HasValue ? lastUpdate : "Usage unavailable";
        }
    }

    internal void SetSession(bool authenticated)
    {
        session?.Cancel();
        session?.Dispose();
        session = authenticated ? new CancellationTokenSource() : null;
        Windows = Array.Empty<CodexUsageWindow>();
        updatedAt = null;
        ordinaryUsageAllowed = null;
        rateLimitReached = false;
        reading = false;
        refreshFailed = false;
        Changed();
        if (authenticated) _ = RefreshAsync();
    }

    internal void ApplyUpdate(CodexUsageSnapshot snapshot)
    {
        if (session == null) return;
        revision++;
        Windows = snapshot.Windows;
        rateLimitReached = snapshot.RateLimitReached;
        updatedAt = DateTimeOffset.Now;
        refreshFailed = false;
        Changed();
    }

    private async Task RefreshAsync()
    {
        CancellationTokenSource? currentSession = session;
        if (currentSession == null || reading) return;
        int previousRevision = revision;
        reading = true;
        Changed();
        CodexUsageSnapshot? result = null;
        using (CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(currentSession.Token))
        {
            request.CancelAfter(TimeSpan.FromSeconds(15));
            try { result = await client.ReadUsageAsync(request.Token).ConfigureAwait(false); }
            catch (Exception) { result = null; } // Show the last reading on transport errors or timeout.
        }
        await dispatcher.InvokeAsync(() =>
        {
            if (session != currentSession) return;
            reading = false;
            // Only the read includes account-wide eligibility. A newer push wins for window data.
            if (result != null) ordinaryUsageAllowed = result.OrdinaryUsageAllowed;
            if (revision == previousRevision && result != null) { ApplyUpdate(result); return; }
            if (revision == previousRevision) refreshFailed = true;
            Changed();
        }).ConfigureAwait(false);
    }

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        RefreshCommand.RaiseCanExecuteChanged();
    }
}
