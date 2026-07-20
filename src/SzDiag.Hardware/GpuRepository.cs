using Microsoft.Data.Sqlite;

namespace SzDiag.Hardware;

/// <summary>SQLite-реализация справочника. Схема идемпотентна; upsert через ON CONFLICT.</summary>
public sealed class GpuRepository : IGpuRepository
{
    private readonly string _connectionString;
    public GpuRepository(string connectionString) => _connectionString = connectionString;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS vendor (
                vendor_id TEXT PRIMARY KEY,
                name      TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS device (
                vendor_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                name      TEXT NOT NULL,
                chip      TEXT NULL,
                model     TEXT NULL,
                source    TEXT NOT NULL DEFAULT 'pci.ids',
                PRIMARY KEY (vendor_id, device_id)
            );
            CREATE TABLE IF NOT EXISTS card (
                sub_vendor_id TEXT NOT NULL,
                sub_device_id TEXT NOT NULL,
                manufacturer  TEXT NULL,
                card_name     TEXT NULL,
                memory_size   TEXT NULL,
                memory_type   TEXT NULL,
                core_clock    TEXT NULL,
                boost_clock   TEXT NULL,
                memory_clock  TEXT NULL,
                power_target  TEXT NULL,
                power_limit   TEXT NULL,
                outputs       TEXT NULL,
                date_compiled TEXT NULL,
                vbios_version TEXT NULL,
                source_url    TEXT NOT NULL,
                PRIMARY KEY (sub_vendor_id, sub_device_id)
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ImportAsync(PciIdsData data, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var vcmd = conn.CreateCommand())
        {
            vcmd.Transaction = tx;
            vcmd.CommandText = """
                INSERT INTO vendor (vendor_id, name) VALUES ($id, $name)
                ON CONFLICT(vendor_id) DO UPDATE SET name = excluded.name;
                """;
            var vid = vcmd.CreateParameter(); vid.ParameterName = "$id"; vcmd.Parameters.Add(vid);
            var vname = vcmd.CreateParameter(); vname.ParameterName = "$name"; vcmd.Parameters.Add(vname);
            foreach (var (id, name) in data.Vendors)
            {
                vid.Value = id; vname.Value = name;
                await vcmd.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var dcmd = conn.CreateCommand())
        {
            dcmd.Transaction = tx;
            dcmd.CommandText = """
                INSERT INTO device (vendor_id, device_id, name, chip, model, source)
                VALUES ($ven, $dev, $name, $chip, $model, 'pci.ids')
                ON CONFLICT(vendor_id, device_id) DO UPDATE SET
                    name = excluded.name, chip = excluded.chip, model = excluded.model, source = excluded.source;
                """;
            var pven = dcmd.CreateParameter(); pven.ParameterName = "$ven"; dcmd.Parameters.Add(pven);
            var pdev = dcmd.CreateParameter(); pdev.ParameterName = "$dev"; dcmd.Parameters.Add(pdev);
            var pname = dcmd.CreateParameter(); pname.ParameterName = "$name"; dcmd.Parameters.Add(pname);
            var pchip = dcmd.CreateParameter(); pchip.ParameterName = "$chip"; dcmd.Parameters.Add(pchip);
            var pmodel = dcmd.CreateParameter(); pmodel.ParameterName = "$model"; dcmd.Parameters.Add(pmodel);
            foreach (var d in data.Devices)
            {
                pven.Value = d.VendorId; pdev.Value = d.DeviceId; pname.Value = d.Name;
                pchip.Value = (object?)d.Chip ?? DBNull.Value;
                pmodel.Value = (object?)d.Model ?? DBNull.Value;
                await dcmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task<string?> LookupVendorAsync(string vendorId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM vendor WHERE vendor_id = $id;";
        cmd.Parameters.AddWithValue("$id", vendorId);
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    public async Task<PciDevice?> LookupDeviceAsync(string vendorId, string deviceId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, chip, model FROM device WHERE vendor_id = $ven AND device_id = $dev;";
        cmd.Parameters.AddWithValue("$ven", vendorId);
        cmd.Parameters.AddWithValue("$dev", deviceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PciDevice(vendorId, deviceId, reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task UpsertDeviceAsync(PciDevice device, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO device (vendor_id, device_id, name, chip, model, source)
            VALUES ($ven, $dev, $name, $chip, $model, 'scraper')
            ON CONFLICT(vendor_id, device_id) DO UPDATE SET
                name = excluded.name, chip = excluded.chip, model = excluded.model, source = excluded.source;
            """;
        cmd.Parameters.AddWithValue("$ven", device.VendorId);
        cmd.Parameters.AddWithValue("$dev", device.DeviceId);
        cmd.Parameters.AddWithValue("$name", device.Name);
        cmd.Parameters.AddWithValue("$chip", (object?)device.Chip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)device.Model ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertCardAsync(ScrapedCard c, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO card (sub_vendor_id, sub_device_id, manufacturer, card_name,
                memory_size, memory_type, core_clock, boost_clock, memory_clock,
                power_target, power_limit, outputs, date_compiled, vbios_version, source_url)
            VALUES ($sv,$sd,$mf,$cn,$ms,$mt,$cc,$bc,$mc,$pt,$pl,$out,$dc,$vb,$url)
            ON CONFLICT(sub_vendor_id, sub_device_id) DO UPDATE SET
                manufacturer=excluded.manufacturer, card_name=excluded.card_name,
                memory_size=excluded.memory_size, memory_type=excluded.memory_type,
                core_clock=excluded.core_clock, boost_clock=excluded.boost_clock,
                memory_clock=excluded.memory_clock, power_target=excluded.power_target,
                power_limit=excluded.power_limit, outputs=excluded.outputs,
                date_compiled=excluded.date_compiled, vbios_version=excluded.vbios_version,
                source_url=excluded.source_url;
            """;
        void P(string n, string? v) => cmd.Parameters.AddWithValue(n, (object?)v ?? DBNull.Value);
        P("$sv", c.SubVendorId); P("$sd", c.SubDeviceId); P("$mf", c.Manufacturer); P("$cn", c.CardName);
        P("$ms", c.MemorySize); P("$mt", c.MemoryType); P("$cc", c.CoreClock); P("$bc", c.BoostClock);
        P("$mc", c.MemoryClock); P("$pt", c.PowerTarget); P("$pl", c.PowerLimit); P("$out", c.Outputs);
        P("$dc", c.DateCompiled); P("$vb", c.VbiosVersion); P("$url", c.SourceUrl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ScrapedCard?> LookupCardAsync(string subVendorId, string subDeviceId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT manufacturer, card_name, memory_size, memory_type, core_clock, boost_clock,
                   memory_clock, power_target, power_limit, outputs, date_compiled, vbios_version, source_url
            FROM card WHERE sub_vendor_id = $sv AND sub_device_id = $sd;
            """;
        cmd.Parameters.AddWithValue("$sv", subVendorId);
        cmd.Parameters.AddWithValue("$sd", subDeviceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new ScrapedCard(subVendorId, subDeviceId,
            S(0), S(1), S(2), S(3), S(4), S(5), S(6), S(7), S(8), S(9), S(10), S(11), S(12)!);
    }
}
