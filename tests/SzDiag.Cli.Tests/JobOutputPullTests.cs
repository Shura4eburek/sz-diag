using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Вывод detached-задачи (`exec --detach`) жил только на клиенте (`ProgramData\szdiag\jobs`)
/// и не переживал потерю/переустановку машины (бэклог п.214, СЗ 161972: единственное приборное
/// доказательство дефекта диска уцелело только пересказом в журнале). `exec --result --save`
/// должен уметь дотянуть `out.txt`/`err.txt` той же задачи на хост через `pull`.</summary>
public class JobOutputPullTests
{
    [Fact]
    public void ClientDir_CombinesJobsRootWithJobId()
    {
        Assert.Equal(@"C:\ProgramData\szdiag\jobs\20260904-153000-abc123",
            JobOutputPull.ClientDir("20260904-153000-abc123"));
    }

    [Fact]
    public void HostLabel_GroupsUnderJobsById()
    {
        // Отдельная подпапка на хосте по jobId, а не метка времени забора — повторный
        // `--save` той же задачи ложится рядом же, а не расползается по времени.
        Assert.Equal("jobs/20260904-153000-abc123", JobOutputPull.HostLabel("20260904-153000-abc123"));
    }
}
