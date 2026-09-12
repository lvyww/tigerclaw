using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using TigerClaw.Core;
using TigerClaw.Overlay;
using TigerClaw.Shared;

internal static class Program
{
    private static int _checks, _placements;
    internal static void Require(bool ok, string message)
    { ++_checks; if (!ok) throw new InvalidOperationException(message); }
    private static CandidatePlacementEnvironment Environment()
    {
        return new CandidatePlacementEnvironment
        {
            Revision = 1, InstanceId = "core-a", Owner = 2, Foreground = 2, Focus = 3, Monitor = 1,
            ProcessId = 10, Dpi = 96, Work = new CandidateRect(0, 0, 1920, 1040),
            OwnerBounds = new CandidateRect(50, 50, 1800, 1000),
            ForegroundBounds = new CandidateRect(0, 0, 1900, 1040)
        };
    }
    private static int Place(ref CandidateOrientation memory, int y, int height,
        CandidatePlacementEnvironment env, int caretHeight = 20, int x = 500, int width = 200)
    {
        int px, py;
        Require(memory.TryPlace(x, y, caretHeight, env, width, height, out px, out py), "valid geometry rejected");
        ++_placements;
        Require(px >= env.Work.Left && py >= env.Work.Top, "escaped top/left");
        if ((long)width + 2 <= (long)env.Work.Right - env.Work.Left)
            Require((long)px + width <= (long)env.Work.Right - 2, "escaped right reserve");
        if ((long)height + 2 <= (long)env.Work.Bottom - env.Work.Top)
            Require((long)py + height <= (long)env.Work.Bottom - 2, "escaped bottom reserve");
        return py;
    }
    private static void Geometry()
    {
        var env = Environment(); var m = new CandidateOrientation();
        Require(Place(ref m, 980, 30, env) == 985 && !m.Above, "first short window not below");
        Require(Place(ref m, 980, 120, env) == 835 && m.Above, "overflow did not flip");
        Require(Place(ref m, 980, 20, env) == 935 && m.Above, "above memory lost across short compositions");
        foreach (int delta in new[] { 1, -1, 3, -3, 2, -2, 0 })
        {
            Require(Place(ref m, 980 + delta, 20, env) == 935 + delta && m.Above, "jitter changed side or froze live coordinates");
            Require(m.ReferenceY == 980, "jitter rewrote reference");
        }
        Place(ref m, 978, 20, env); Require(m.Above, "cumulative move released too soon");
        Place(ref m, 976, 20, env); Require(!m.Above, "cumulative upward movement swallowed");
        Place(ref m, 980, 120, env); Place(ref m, 1000, 20, env);
        Require(m.Above && m.ReferenceY == 1000, "downward move forgot side/reference");
        Place(ref m, 996, 20, env); Require(!m.Above, "upward move failed to re-evaluate");
        Place(ref m, 980, 120, env); Place(ref m, 976, 120, env);
        Require(m.Above, "upward move forced a non-fitting lower side");
        Place(ref m, 980, 20, env, 980); Require(!m.Above, "memory defeated space above constraint");
        Place(ref m, 980, 120, env);
        int x, y; int reference = m.ReferenceY;
        Require(!m.TryPlace(1, 1, 0, env, 20, 20, out x, out y), "zero-height caret accepted");
        Require(!m.TryPlace(1, 1, 20, env, -1, 20, out x, out y), "negative size accepted");
        var empty = env; empty.Work = default(CandidateRect);
        Require(!m.TryPlace(1, 1, 20, empty, 20, 20, out x, out y), "empty area accepted");
        Require(m.ReferenceY == reference && m.Above, "invalid layout polluted memory");
        for (int kind = 0; kind < 12; ++kind)
        {
            var memory = new CandidateOrientation(); Place(ref memory, 980, 120, env); var changed = env;
            switch (kind)
            {
                case 0: ++changed.Revision; break; case 1: changed.InstanceId = "core-b"; break;
                case 2: ++changed.Owner; break; case 3: ++changed.Foreground; break;
                case 4: ++changed.Focus; break; case 5: ++changed.Monitor; break;
                case 6: ++changed.ProcessId; break; case 7: changed.Dpi = 144; break;
                case 8: --changed.Work.Bottom; break; case 9: ++changed.OwnerBounds.Top; break;
                case 10: ++changed.ForegroundBounds.Left; break; case 11: changed.StartMenu = true; break;
            }
            Place(ref memory, 980, 20, changed); Require(!memory.Above, "environment change inherited old direction");
        }
        var other = new CandidateOrientation(); Place(ref other, 980, 20, env);
        Require(!other.Above, "independent input environments shared state");
        m.Reset(); Place(ref m, 980, 20, env); Require(!m.Above, "explicit reset retained above");
        var areas = new[] { new CandidateRect(0, 0, 1920, 1040), new CandidateRect(-1920, 0, 0, 1040),
            new CandidateRect(0, -1080, 1920, -40), new CandidateRect(-2560, -1440, 0, -40),
            new CandidateRect(1920, 120, 4480, 1520), new CandidateRect(0, 48, 1920, 1080),
            new CandidateRect(48, 0, 1920, 1080), new CandidateRect(0, 0, 1872, 1080) };
        foreach (var area in areas) foreach (int dpi in new[] { 96, 120, 144, 192 })
        {
            var e = env; e.Work = area; e.Dpi = dpi;
            var sticky = new CandidateOrientation(); var perWord = new CandidateOrientation();
            int flips = 0, legacyFlips = 0; bool last = false, old = false;
            int smallHeight = 16 * dpi / 96, bigHeight = 150 * dpi / 96, line = 24 * dpi / 96;
            int caret = area.Bottom - 80;
            for (int word = 0; word < 100; ++word)
            {
                perWord.Reset();
                foreach (int height in new[] { smallHeight, bigHeight, smallHeight, bigHeight + 10 })
                {
                    Place(ref sticky, caret, height, e, line, area.Left + 50);
                    Place(ref perWord, caret, height, e, line, area.Left + 50);
                    if (sticky.Above != last) ++flips; last = sticky.Above;
                    if (perWord.Above != old) ++legacyFlips; old = perWord.Above;
                }
            }
            Require(flips == 1 && legacyFlips == 199, "word-sequence direction transitions incorrect");
            int epsilon = CandidateOrientation.Tolerance(line, line, dpi);
            Require(epsilon < line && epsilon <= line / 4, "jitter band swallows full line");
            Place(ref sticky, caret - epsilon, smallHeight, e, line, area.Left + 50);
            Require(sticky.Above, "threshold should be inclusive");
            Place(ref sticky, caret - epsilon - 1, smallHeight, e, line, area.Left + 50);
            Require(!sticky.Above, "upward threshold ignored");
            for (int height = 1; height < 1800; height += 7)
                Place(ref sticky, caret, height, e, line, area.Right - 10, 300);
        }
        Require(CandidateOrientation.Tolerance(3, 20, 192) == 0, "tiny caret hides a line");
        var extreme = env; extreme.Work = new CandidateRect(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue);
        Place(ref m, int.MaxValue, 120, extreme, 20, 0);
        Place(ref m, int.MinValue + 20, 120, extreme, 20, 0);
        var random = new Random(20260912);
        for (int i = 0; i < 100000; ++i)
        {
            var e = env; e.Work = areas[i % areas.Length];
            int caret = e.Work.Top + random.Next(-100, e.Work.Bottom - e.Work.Top + 100);
            Place(ref m, caret, random.Next(1, 2200), e, random.Next(1, 80),
                e.Work.Left + random.Next(-100, e.Work.Right - e.Work.Left + 100), random.Next(1, 2600));
        }
    }
    private static void TransportAndEvents()
    {
        var tracker = new CandidateEnvironmentTracker(); tracker.ObserveMode(false, true, true);
        long epoch = tracker.Revision;
        for (int i = 0; i < 100; ++i) tracker.ObserveMode(false, true, true);
        Require(tracker.Revision == epoch, "ordinary updates advanced environment");
        tracker.ObserveMode(false, false, true); tracker.ObserveMode(false, true, true);
        Require(tracker.Revision > epoch, "coalesced off/on transition invisible");
        Require(tracker.InstanceId != new CandidateEnvironmentTracker().InstanceId, "Core restart identity reused");
        epoch = tracker.Revision; tracker.Invalidate(); Require(tracker.Revision > epoch, "explicit context hint lost");
        var state = new OverlayUiState { IsChinese = true, CandidateEnvironmentId = "new-core",
            CandidateEnvironmentRevision = 20, CandidateOwnerHwnd = -2147483616,
            CandidateOwnerProcessId = 1234, CandidateEnvironmentActive = true };
        var serializer = new DataContractJsonSerializer(typeof(OverlayUiState));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, state); stream.Position = 0;
            var copy = (OverlayUiState)serializer.ReadObject(stream);
            Require(copy.CandidateEnvironmentId == state.CandidateEnvironmentId && copy.CandidateEnvironmentRevision == 20 &&
                copy.CandidateOwnerHwnd == state.CandidateOwnerHwnd && copy.CandidateEnvironmentActive, "new fields lost in DCS roundtrip");
        }
        using (var oldStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"IsChinese\":true,\"CandidateVisible\":true}")))
        {
            var oldState = (OverlayUiState)serializer.ReadObject(oldStream);
            Require(oldState.CandidateEnvironmentRevision == 0 && oldState.IsChinese, "old MMF payload rejected");
        }
        var session = new OverlaySessionState(); session.Update(state);
        Require(session.Update(state) == OverlayUiChangeFlags.None, "unchanged snapshot repeatedly invalidates");
        ++state.CandidateEnvironmentRevision;
        var changed = session.Update(state);
        Require((changed & (OverlayUiChangeFlags.Content | OverlayUiChangeFlags.Position)) ==
            (OverlayUiChangeFlags.Content | OverlayUiChangeFlags.Position), "hidden environment change not dispatched");
        Require(session.Update(state) == OverlayUiChangeFlags.None, "environment fields not cloned");
        ++state.CaretHeight; Require((session.Update(state) & OverlayUiChangeFlags.Position) != 0, "caret-height-only layout ignored");
    }
    private static int Main()
    {
        try
        {
            Geometry(); TransportAndEvents();
#if CANDIDATE_PROTOCOL_TESTS
            ProtocolTests.Run();
#endif
            Console.WriteLine("{\"status\":\"passed\",\"placements\":" + _placements + ",\"checks\":" + _checks +
                ",\"physical_input_tested\":false}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
