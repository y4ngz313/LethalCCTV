namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal enum SurveillanceFeedKind : byte
    {
        Bodycam = 0,
        Facility = 1,
    }

    internal readonly struct SurveillanceFeedAddress
    {
        internal SurveillanceFeedAddress(SurveillanceFeedKind kind, int localIndex)
        {
            Kind = kind;
            LocalIndex = localIndex;
        }

        internal SurveillanceFeedKind Kind { get; }
        internal int LocalIndex { get; }
    }

    internal static class SurveillanceFeedOrder
    {
        internal static int TotalCount(int bodycamCount, int facilityCount)
        {
            return ClampCount(bodycamCount) + ClampCount(facilityCount);
        }

        internal static bool TryResolve(int globalIndex, int bodycamCount, int facilityCount, out SurveillanceFeedAddress address)
        {
            int bodycams = ClampCount(bodycamCount);
            int facilities = ClampCount(facilityCount);
            if (globalIndex < 0 || globalIndex >= bodycams + facilities)
            {
                address = default;
                return false;
            }

            address = globalIndex < bodycams
                ? new SurveillanceFeedAddress(SurveillanceFeedKind.Bodycam, globalIndex)
                : new SurveillanceFeedAddress(SurveillanceFeedKind.Facility, globalIndex - bodycams);
            return true;
        }

        internal static int ToGlobalIndex(SurveillanceFeedKind kind, int localIndex, int bodycamCount, int facilityCount)
        {
            int bodycams = ClampCount(bodycamCount);
            int facilities = ClampCount(facilityCount);
            if (localIndex < 0) return -1;
            if (kind == SurveillanceFeedKind.Bodycam)
                return localIndex < bodycams ? localIndex : -1;
            return localIndex < facilities ? bodycams + localIndex : -1;
        }

        internal static int WrapIndex(int requestedIndex, int totalCount)
        {
            int total = ClampCount(totalCount);
            if (total == 0) return -1;
            return ((requestedIndex % total) + total) % total;
        }

        private static int ClampCount(int count)
        {
            return count > 0 ? count : 0;
        }
    }
}
