namespace SzDiag.Contracts;

/// <summary>Как подключиться к клиенту. Строку команды собирает CLI: приватный ключ лежит
/// рядом с ним, а не с hub. <paramref name="Ssh"/> — подсказка hub на случай, когда ключ не
/// нужен.</summary>
/// <param name="Ip">Адрес коннекта, как его видит hub. В туннельном режиме подключаться по
/// нему нельзя — адресом служит <paramref name="AccessHost"/>.</param>
/// <param name="AccessHost">Имя quick tunnel'а, если поднят.</param>
/// <param name="AccessMode">См. <see cref="SzDiag.Contracts.AccessMode"/>.</param>
/// <param name="Unavailable">Заполнено, когда подключиться нельзя. Печатать в этом случае
/// неработающую строку — врать пользователю: `target` обязан назвать причину.</param>
/// <param name="KnownHostsPath">Файл с приколотым host-ключом этой СЗ (лежит на том же
/// боксе, что и CLI). Задан — ходим со строгой проверкой; null — агент ключа не присылал.</param>
public sealed record TargetInfo(string Sz, string Ip, string User, string Ssh,
    string? AccessHost = null, string? AccessMode = null, string? Unavailable = null,
    string? KnownHostsPath = null);
