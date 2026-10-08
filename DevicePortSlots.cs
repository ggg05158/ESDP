using ESDP.Models;

namespace ESDP;

// Allocate transactionally: an unsuccessful request never moves existing ports.
internal static class DevicePortSlots
{
    public static bool TryChooseSideForNewPort(
        IReadOnlyList<Port> existing, double width, double height,
        int preferredStartIndex, out PortSide side)
    {
        var sides = Enum.GetValues<PortSide>();
        for (int offset = 0; offset < sides.Length; offset++)
        {
            var candidateSide = sides[(preferredStartIndex + offset) % sides.Length];
            var probe = existing.Select(CloneForAllocation).ToList();
            probe.Add(new Port { Side = candidateSide, Position = -1 });
            if (TryAssign(probe, width, height, out _))
            {
                side = candidateSide;
                return true;
            }
        }

        side = default;
        return false;
    }

    private static Port CloneForAllocation(Port port) => new()
    {
        Id = port.Id,
        Side = port.Side,
        Position = port.Position
    };

    public static bool TryAssign(IReadOnlyList<Port> ports, double width, double height,
        out Dictionary<string, double> positions)
    {
        positions = new();
        foreach (PortSide side in Enum.GetValues<PortSide>())
        {
            double length = side is PortSide.Top or PortSide.Bottom ? width : height;
            int count = DevicePreviewRenderer.GetPortSlotCount(length);
            var members = ports.Where(p => p.Side == side).ToList();
            if (members.Count > count) return false;
            var occupied = new HashSet<int>();
            foreach (var port in members.Where(p => p.Position >= 0))
            {
                int slot = DevicePreviewRenderer.GetPortSlotIndex(port.Position, length);
                if (!occupied.Add(slot)) return false;
                positions[port.Id] = (slot + .5) * DevicePreviewRenderer.PortSlotSize / length;
            }
            foreach (var port in members.Where(p => p.Position < 0))
            {
                double desired = DevicePreviewRenderer.GetPortSlotFraction(
                    port, members.IndexOf(port), members.Count, length);
                int desiredSlot = DevicePreviewRenderer.GetPortSlotIndex(desired, length);
                int slot = Enumerable.Range(0, count).Where(i => !occupied.Contains(i))
                    .OrderBy(i => Math.Abs(i - desiredSlot)).ThenBy(i => i).First();
                occupied.Add(slot);
                positions[port.Id] = (slot + .5) * DevicePreviewRenderer.PortSlotSize / length;
            }
        }
        return true;
    }
}
