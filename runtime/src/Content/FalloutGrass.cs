using System.Buffers.Binary;
using System.Text;

namespace OpenNV.Runtime.Content;

internal sealed record FalloutGrass(
    FalloutFormKey Form,
    string EditorId,
    string ModelPath,
    byte Density,
    byte MinSlope,
    byte MaxSlope,
    float PositionRange,
    float HeightRange,
    float ColorRange,
    float WavePeriod,
    uint Flags)
{
    internal bool UniformScaling => (Flags & 0x02) != 0;
    internal bool FitToSlope => (Flags & 0x04) != 0;

    internal static FalloutGrass Read(FalloutPluginStack stack, FalloutFormKey key)
    {
        var record = stack.GetEffective(key);
        if (record.Signature != "GRAS")
            throw new InvalidDataException($"Resolved landscape grass {key} is {record.Signature}, not GRAS.");
        var rows = record.ReadSubrecords().ToArray();
        var models = rows.Where(row => row.Signature == "MODL").ToArray();
        if (models.Length != 1)
            throw new InvalidDataException($"GRAS {key} requires one MODL subrecord.");
        var data = rows.Where(row => row.Signature == "DATA").ToArray();
        if (data.Length != 1 || data[0].Data.Length < 32)
            throw new InvalidDataException($"GRAS {key} requires a 32-byte DATA subrecord.");
        var bytes = data[0].Data.Span;
        var position = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(12, 4));
        var height = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(16, 4));
        var color = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(20, 4));
        var wave = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(24, 4));
        if (!float.IsFinite(position) || position <= 0.0f)
            throw new InvalidDataException($"GRAS {key} has an invalid position range.");
        var edidRows = rows.Where(row => row.Signature == "EDID").ToArray();
        var editorId = edidRows.Length == 0 ? "" : Encoding.ASCII.GetString(edidRows[0].Data.Span).TrimEnd('\0');
        return new FalloutGrass(
            key,
            editorId,
            NormalizeModelPath(Encoding.ASCII.GetString(models[0].Data.Span).TrimEnd('\0')),
            bytes[0],
            bytes[1],
            bytes[2],
            position,
            float.IsFinite(height) ? height : 0.0f,
            float.IsFinite(color) ? color : 0.0f,
            float.IsFinite(wave) ? wave : 0.0f,
            bytes[28]);
    }

    private static string NormalizeModelPath(string source)
    {
        var path = source.Replace('/', '\\').TrimStart('\\');
        return (path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? path : "meshes\\" + path)
            .ToLowerInvariant();
    }
}
