namespace SzDiag.Contracts;

/// <summary>Запуск прогона. Метка конфигурации обязательна: прогон без неё через неделю
/// нечитаем — непонятно, что с чем сравнивать (на СЗ 160697 результаты «профиль против стока»
/// потерялись именно так).</summary>
/// <param name="Filter">Фильтр набора тестов (null — весь набор).</param>
/// <param name="Config">Метка конфигурации, напр. «EXPO 6000, штатний БЖ».</param>
/// <param name="SameConfig">Повторить прогон на последней сохранённой метке.</param>
/// <param name="Schedule">Профиль расписания OCCT (бэклог п.124/#60, `--schedule &lt;имя&gt;`:
/// default/smoke/long/infinite — см. <see cref="OcctScheduleProfiles"/>) — null — как в
/// testsuite.json. Раньше длину прогона выбирали, подменяя файл на клиенте руками.</param>
public sealed record TestRunRequest(string? Filter, string? Config, bool SameConfig, string? Schedule = null);
