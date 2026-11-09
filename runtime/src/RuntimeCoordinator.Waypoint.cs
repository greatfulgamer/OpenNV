using System.Text.Json;
using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Formats.Gamebryo;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime;

public partial class RuntimeCoordinator
{
    private bool _waypointAnchorLock;

    // A waypoint is the engine-neutral part of a retail save: the CELL/WRLD the
    // player was in and the player pose. Position/rotation stay in Gamebryo
    // units and radians; the same GamebryoCoordinate path used by authored
    // placements performs the conversion into Godot space.
    private FalloutReferencePlacement ResolveWaypointPlacement(FalloutPluginStack stack, string path)
    {
        ArgumentNullException.ThrowIfNull(stack);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema", out var schema) ||
            schema.ValueKind != JsonValueKind.String ||
            schema.GetString() != "opennv-waypoint/v1")
            throw new InvalidDataException($"Waypoint {path} has an unsupported schema.");
        var target = ReadFormKey(root.GetProperty("target"), path);
        var position = ReadFloats(root.GetProperty("position"), 3, "position", path);
        var rotation = ReadFloats(root.GetProperty("rotationRadians"), 3, "rotationRadians", path);
        var record = stack.GetEffective(target);
        FalloutFormKey cell;
        if (record.Signature == "CELL")
        {
            cell = target;
        }
        else if (record.Signature == "WRLD")
        {
            // Exterior positions select their authored grid cell exactly as
            // ordinary streaming does; the WRLD only names the worldspace.
            cell = new FalloutExteriorGrid(stack).SpatialCell(target, position[0], position[1]);
        }
        else
        {
            throw new InvalidDataException(
                $"Waypoint target {target} is a {record.Signature} record, not CELL or WRLD.");
        }
        var placement = new FalloutReferencePlacement(cell, position, rotation);
        placement.Validate();
        GD.Print(
            $"OPENNV_WAYPOINT_READY source={path} target={target} signature={record.Signature} cell={cell} " +
            $"position={position[0]:F3},{position[1]:F3},{position[2]:F3}");
        return placement;
    }

    private (float[] Position, float[] Rotation) WaypointGodotPose(FalloutReferencePlacement placement)
    {
        var units = _configuration.World.GameUnitsToMeters;
        var position = GamebryoCoordinate.ConvertVector(
            new Vector3(placement.Position[0], placement.Position[1], placement.Position[2])) * units;
        var rotation = GamebryoCoordinate.ConvertReferenceEuler(
                new Vector3(placement.RotationRadians[0], placement.RotationRadians[1], placement.RotationRadians[2]), 1)
            .GetRotationQuaternion().Normalized();
        return ([position.X, position.Y, position.Z], [rotation.X, rotation.Y, rotation.Z, rotation.W]);
    }

    private static FalloutFormKey ReadFormKey(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("ownerPlugin", out var owner) ||
            owner.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("objectId", out var objectId) ||
            objectId.ValueKind != JsonValueKind.Number ||
            !objectId.TryGetUInt32(out var id) ||
            id == 0 || id > FalloutFormKey.ObjectIdMask)
            throw new InvalidDataException($"Waypoint {path} has an invalid form key.");
        return new FalloutFormKey(owner.GetString()!, id);
    }

    private static float[] ReadFloats(JsonElement element, int count, string name, string path)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != count)
            throw new InvalidDataException($"Waypoint {path} has an invalid {name}.");
        var values = new float[count];
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out var value) || !float.IsFinite(value))
                throw new InvalidDataException($"Waypoint {path} has a non-finite {name} component.");
            values[index++] = value;
        }
        return values;
    }
}
