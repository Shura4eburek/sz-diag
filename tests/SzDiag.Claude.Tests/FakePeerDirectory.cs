using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

internal sealed class FakePeerDirectory : IPeerDirectory
{
    public List<PeerInfo> All { get; } = new();
    public Dictionary<string, string> Summaries { get; } = new();

    public IReadOnlyList<PeerInfo> Peers(string askerKey) => All.Where(p => p.Key != askerKey).ToList();
    public string? Summary(string key) => Summaries.GetValueOrDefault(key);
}
