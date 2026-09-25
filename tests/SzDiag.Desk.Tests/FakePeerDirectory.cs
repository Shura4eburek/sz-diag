using SzDiag.Claude;

namespace SzDiag.Desk.Tests;

internal sealed class FakePeerDirectory : IPeerDirectory
{
    public List<PeerInfo> All { get; } = new();
    public IReadOnlyList<PeerInfo> Peers(string askerKey) => All.Where(p => p.Key != askerKey).ToList();
    public string? Summary(string key) => null;
}
