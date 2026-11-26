using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Cameo.Embed;

/// <summary>Bitfield definition inside a 32-bit hardware peripheral register.</summary>
public sealed record RegFieldSpec(string Name, int Lsb, int Width, string Access = "read-write");

/// <summary>32-bit memory-mapped hardware register definition.</summary>
public sealed record RegSpec(
    string Name,
    uint Offset,
    string Access,
    IReadOnlyList<RegFieldSpec> Fields);

/// <summary>Hardware peripheral block definition with base address and register list.</summary>
public sealed record PeripheralSpec(
    string Name,
    ulong BaseAddress,
    IReadOnlyList<RegSpec> Registers);

/// <summary>
/// Generates zero-heap, statically typed Cameo.Embed peripheral register-map overlays from
/// CMSIS-SVD XML files or built-in NexRT board specs (<c>riscue</c>, <c>mps2</c>).
/// </summary>
public static class SvdRegMapGenerator
{
    /// <summary>Parses a CMSIS-SVD XML document into peripheral/register/bitfield specifications.</summary>
    public static IReadOnlyList<PeripheralSpec> ParseSvd(string svdXml)
    {
        XDocument doc = XDocument.Parse(svdXml);
        XElement? peripheralsEl = doc.Root?.Element("peripherals");
        if (peripheralsEl is null)
            return [];

        var byName = new Dictionary<string, PeripheralSpec>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PeripheralSpec>();

        foreach (XElement pEl in peripheralsEl.Elements("peripheral"))
        {
            string name = (pEl.Element("name")?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name)) continue;

            ulong baseAddr = ParseUInt64(pEl.Element("baseAddress")?.Value);
            string? derivedFrom = pEl.Attribute("derivedFrom")?.Value;

            var regs = new List<RegSpec>();
            if (!string.IsNullOrEmpty(derivedFrom) && byName.TryGetValue(derivedFrom, out PeripheralSpec? parent))
                regs.AddRange(parent.Registers);

            XElement? regsEl = pEl.Element("registers");
            if (regsEl is not null)
            {
                foreach (XElement rEl in regsEl.Elements("register"))
                {
                    string rName = (rEl.Element("name")?.Value ?? "").Trim();
                    if (string.IsNullOrEmpty(rName)) continue;

                    uint offset = (uint)ParseUInt64(rEl.Element("addressOffset")?.Value);
                    string access = (rEl.Element("access")?.Value ?? "read-write").Trim();
                    var fields = new List<RegFieldSpec>();

                    XElement? fieldsEl = rEl.Element("fields");
                    if (fieldsEl is not null)
                    {
                        foreach (XElement fEl in fieldsEl.Elements("field"))
                        {
                            string fName = (fEl.Element("name")?.Value ?? "").Trim();
                            if (string.IsNullOrEmpty(fName)) continue;
                            (int lsb, int width) = ParseBitRange(fEl);
                            if (width <= 0 || lsb < 0 || lsb >= 32) continue;
                            string fAccess = (fEl.Element("access")?.Value ?? access).Trim();
                            fields.Add(new RegFieldSpec(fName, lsb, width, fAccess));
                        }
                    }

                    regs.Add(new RegSpec(rName, offset, access, fields));
                }
            }

            var spec = new PeripheralSpec(name, baseAddr, regs);
            byName[name] = spec;
            result.Add(spec);
        }

        return result;
    }

    /// <summary>Returns built-in NexRT board peripheral specifications (<c>riscue</c> or <c>mps2</c>).</summary>
    public static IReadOnlyList<PeripheralSpec> GetBuiltInBoardSpec(string boardName)
    {
        return boardName.Trim().ToLowerInvariant() switch
        {
            "riscue" =>
            [
                new PeripheralSpec("UARTLITE", 0x10000000UL,
                [
                    new RegSpec("RX_FIFO", 0x00u, "read-only", [new RegFieldSpec("DATA", 0, 8, "read-only")]),
                    new RegSpec("TX_FIFO", 0x04u, "write-only", [new RegFieldSpec("DATA", 0, 8, "write-only")]),
                    new RegSpec("STAT", 0x08u, "read-only",
                    [
                        new RegFieldSpec("RX_VALID", 0, 1, "read-only"),
                        new RegFieldSpec("RX_FULL", 1, 1, "read-only"),
                        new RegFieldSpec("TX_EMPTY", 2, 1, "read-only"),
                        new RegFieldSpec("TX_FULL", 3, 1, "read-only"),
                    ]),
                    new RegSpec("CTRL", 0x0Cu, "write-only",
                    [
                        new RegFieldSpec("RST_TX", 0, 1, "write-only"),
                        new RegFieldSpec("RST_RX", 1, 1, "write-only"),
                        new RegFieldSpec("INTR_EN", 4, 1, "write-only"),
                    ]),
                ]),
                new PeripheralSpec("HWSCHED", 0x40010000UL,
                [
                    new RegSpec("CTRL", 0x00u, "read-write",
                    [
                        new RegFieldSpec("ENABLE", 0, 1),
                        new RegFieldSpec("TICKLESS", 1, 1),
                        new RegFieldSpec("RR_ENABLE", 2, 1),
                        new RegFieldSpec("SEALED", 3, 1),
                        new RegFieldSpec("IRQ_EN", 4, 1),
                    ]),
                    new RegSpec("STATUS", 0x04u, "read-only",
                    [
                        new RegFieldSpec("CURRENT_TID", 0, 4, "read-only"),
                        new RegFieldSpec("CURRENT_VALID", 4, 1, "read-only"),
                        new RegFieldSpec("NEXT_TID", 8, 4, "read-only"),
                        new RegFieldSpec("NEXT_VALID", 12, 1, "read-only"),
                        new RegFieldSpec("PREEMPT_REQ", 13, 1, "read-only"),
                        new RegFieldSpec("BUDGET_FAULT", 15, 1, "read-only"),
                        new RegFieldSpec("SEU_FAULT", 16, 1, "read-only"),
                    ]),
                    new RegSpec("READY_MASK", 0x14u, "read-only", []),
                    new RegSpec("FAULT_MASK", 0x18u, "read-write", []),
                    new RegSpec("SEU_COUNT", 0x1Cu, "read-only", []),
                    new RegSpec("VERSION", 0x3Cu, "read-only", []),
                ]),
            ],
            "mps2" or "mps2-an385" =>
            [
                new PeripheralSpec("CMSDK_TIMER0", 0x40000000UL,
                [
                    new RegSpec("CTRL", 0x00u, "read-write",
                    [
                        new RegFieldSpec("ENABLE", 0, 1),
                        new RegFieldSpec("EXTIN_EN", 1, 1),
                        new RegFieldSpec("EXTIN_CLK", 2, 1),
                        new RegFieldSpec("IRQ_EN", 3, 1),
                    ]),
                    new RegSpec("VALUE", 0x04u, "read-write", []),
                    new RegSpec("RELOAD", 0x08u, "read-write", []),
                    new RegSpec("INTSTATUS", 0x0Cu, "read-write",
                    [
                        new RegFieldSpec("PENDING", 0, 1),
                    ]),
                ]),
                new PeripheralSpec("CMSDK_UART0", 0x40004000UL,
                [
                    new RegSpec("DATA", 0x00u, "read-write", [new RegFieldSpec("BYTE", 0, 8)]),
                    new RegSpec("STATE", 0x04u, "read-only",
                    [
                        new RegFieldSpec("TX_FULL", 0, 1, "read-only"),
                        new RegFieldSpec("RX_FULL", 1, 1, "read-only"),
                        new RegFieldSpec("TX_OVERRUN", 2, 1, "read-only"),
                        new RegFieldSpec("RX_OVERRUN", 3, 1, "read-only"),
                    ]),
                    new RegSpec("CTRL", 0x08u, "read-write",
                    [
                        new RegFieldSpec("TX_EN", 0, 1),
                        new RegFieldSpec("RX_EN", 1, 1),
                    ]),
                    new RegSpec("BAUDDIV", 0x10u, "read-write", []),
                ]),
            ],
            _ => throw new ArgumentException($"Unknown built-in board spec '{boardName}'. Expected 'riscue' or 'mps2'."),
        };
    }

    /// <summary>Generates typed, zero-heap Cameo.Embed peripheral overlay code from a CMSIS-SVD XML string.</summary>
    public static string GenerateFromSvd(string svdXml, string sourceLabel = "device.svd") =>
        EmitCameoModule(ParseSvd(svdXml), sourceLabel);

    /// <summary>Generates typed, zero-heap Cameo.Embed peripheral overlay code for a built-in NexRT board.</summary>
    public static string GenerateBuiltIn(string boardName) =>
        EmitCameoModule(GetBuiltInBoardSpec(boardName), $"built-in:{boardName}");

    /// <summary>Emits a Cameo.Embed source module containing base/offset constants and typed peripheral overlay records.</summary>
    public static string EmitCameoModule(IReadOnlyList<PeripheralSpec> peripherals, string sourceLabel = "regmap")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- Generated by Cameo.Embed SvdRegMapGenerator from {sourceLabel}");
        sb.AppendLine("-- Zero-heap typed MMIO peripheral overlays backed by Ptr.read32_at / write32_at / bits32_at / rmw_bits32_at.");
        sb.AppendLine();

        foreach (PeripheralSpec p in peripherals)
        {
            string pIdent = SanitizeIdent(p.Name);
            string pUpper = pIdent.ToUpperInvariant();
            string recName = ToPascalCase(pIdent) + "Regs";

            sb.AppendLine($"let {pUpper}_BASE: integer = 0x{p.BaseAddress:X8}");
            foreach (RegSpec r in p.Registers)
            {
                string rUpper = SanitizeIdent(r.Name).ToUpperInvariant();
                sb.AppendLine($"let {pUpper}_{rUpper}_OFF: integer = 0x{r.Offset:X2}");
            }
            sb.AppendLine();

            sb.AppendLine($"record {recName} {{");
            sb.AppendLine("    base: Ptr");
            sb.AppendLine("}");
            sb.AppendLine();
            string pLower = pIdent.ToLowerInvariant();
            sb.AppendLine($"fn {pLower}_open(base_addr: integer) -> {recName} {{");
            sb.AppendLine($"    return {{ base = ptr(base_addr) }}");
            sb.AppendLine("}");
            foreach (RegSpec r in p.Registers)
            {
                string rLower = SanitizeIdent(r.Name).ToLowerInvariant();
                bool canRead = !r.Access.Equals("write-only", StringComparison.OrdinalIgnoreCase);
                bool canWrite = !r.Access.Equals("read-only", StringComparison.OrdinalIgnoreCase);

                if (canRead)
                {
                    sb.AppendLine($"fn {pLower}_read_{rLower}(r: {recName}) -> integer {{");
                    sb.AppendLine($"    return r.base.read32_at(0x{r.Offset:X2})");
                    sb.AppendLine("}");
                }
                if (canWrite)
                {
                    sb.AppendLine($"fn {pLower}_write_{rLower}(r: {recName}, val: integer) -> boolean {{");
                    sb.AppendLine($"    return r.base.write32_at(0x{r.Offset:X2}, val)");
                    sb.AppendLine("}");
                }
                foreach (RegFieldSpec f in r.Fields)
                {
                    string fLower = SanitizeIdent(f.Name).ToLowerInvariant();
                    bool fRead = canRead && !f.Access.Equals("write-only", StringComparison.OrdinalIgnoreCase);
                    bool fWrite = canWrite && !f.Access.Equals("read-only", StringComparison.OrdinalIgnoreCase);
                    if (fRead)
                    {
                        sb.AppendLine($"fn {pLower}_get_{rLower}_{fLower}(r: {recName}) -> integer {{");
                        sb.AppendLine($"    return r.base.bits32_at(0x{r.Offset:X2}, {f.Lsb}, {f.Width})");
                        sb.AppendLine("}");
                    }
                    if (fWrite && fRead)
                    {
                        sb.AppendLine($"fn {pLower}_set_{rLower}_{fLower}(r: {recName}, val: integer) -> boolean {{");
                        sb.AppendLine($"    let _, ok = r.base.rmw_bits32_at(0x{r.Offset:X2}, {f.Lsb}, {f.Width}, val)");
                        sb.AppendLine("    return ok");
                        sb.AppendLine("}");
                    }
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static (int Lsb, int Width) ParseBitRange(XElement fEl)
    {
        string? bitRange = fEl.Element("bitRange")?.Value;
        if (!string.IsNullOrWhiteSpace(bitRange))
        {
            string trimmed = bitRange.Trim().Trim('[', ']');
            string[] parts = trimmed.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int msb) &&
                int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lsb) &&
                msb >= lsb)
            {
                return (lsb, msb - lsb + 1);
            }
        }
        string? offStr = fEl.Element("bitOffset")?.Value;
        string? widthStr = fEl.Element("bitWidth")?.Value;
        if (offStr is not null && widthStr is not null &&
            int.TryParse(offStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int off) &&
            int.TryParse(widthStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w))
        {
            return (off, w);
        }
        string? lsbStr = fEl.Element("lsb")?.Value;
        string? msbStr = fEl.Element("msb")?.Value;
        if (lsbStr is not null && msbStr is not null &&
            int.TryParse(lsbStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) &&
            int.TryParse(msbStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) &&
            m >= l)
        {
            return (l, m - l + 1);
        }
        return (0, 0);
    }

    private static ulong ParseUInt64(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0UL;
        string t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hx) ? hx : 0UL;
        return ulong.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong dec) ? dec : 0UL;
    }

    private static string SanitizeIdent(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else sb.Append('_');
        }
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string ToPascalCase(string ident)
    {
        var sb = new StringBuilder(ident.Length);
        bool upperNext = true;
        foreach (char c in ident)
        {
            if (c == '_') { upperNext = true; continue; }
            sb.Append(upperNext ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            upperNext = false;
        }
        return sb.Length == 0 ? "Periph" : sb.ToString();
    }
}
