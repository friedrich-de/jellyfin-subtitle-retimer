using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Jimaku;
using Jellyfin.Plugin.Retimer.Retimer;

namespace Jellyfin.Plugin.Retimer.Tests.Support;

public sealed class FakeJimaku : IJimakuClient
{
    public JimakuException? Failure { get; set; }

    public List<JimakuEntryQuery> Lookups { get; } = [];

    public List<JimakuEntryQuery> Searches { get; } = [];

    public List<(long EntryId, int? Episode)> FileRequests { get; } = [];

    public Task<IReadOnlyList<JimakuEntry>> SearchEntriesAsync(JimakuEntryQuery query, CancellationToken cancellationToken)
    {
        Searches.Add(query);
        return Failure is null ? Task.FromResult<IReadOnlyList<JimakuEntry>>([]) : Task.FromException<IReadOnlyList<JimakuEntry>>(Failure);
    }

    public Task<IReadOnlyList<JimakuEntry>> FindEntriesAsync(JimakuEntryQuery lookup, CancellationToken cancellationToken)
    {
        Lookups.Add(lookup);
        return Failure is null
            ? Task.FromResult<IReadOnlyList<JimakuEntry>>([new JimakuEntry { Id = 4321, Name = "Sousou no Frieren", LastModified = DateTimeOffset.UnixEpoch }])
            : Task.FromException<IReadOnlyList<JimakuEntry>>(Failure);
    }

    public Task<IReadOnlyList<JimakuFile>> GetFilesAsync(long entryId, int? episode, CancellationToken cancellationToken)
    {
        FileRequests.Add((entryId, episode));
        return Failure is null
            ? Task.FromResult<IReadOnlyList<JimakuFile>>([new JimakuFile { Name = "03.srt", Url = "https://jimaku.cc/x", Size = 1 }])
            : Task.FromException<IReadOnlyList<JimakuFile>>(Failure);
    }
}

public sealed class FakeRetimer : IRetimerClient
{
    public RetimerConnectionStatus Status { get; set; } = RetimerConnectionStatus.Ok;

    public Task<RetimerConnectionStatus> CheckConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
}
