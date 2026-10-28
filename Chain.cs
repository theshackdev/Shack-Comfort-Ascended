using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TheShack.ComfortAscended
{
    // The game's private members this mod uses, resolved once at startup (Init) so a game update that renames one
    // fails loudly in the log instead of in the middle of a comfort check.
    internal static class Access
    {
        private static FieldInfo allComfortPiecesField;
        private static HashSet<Piece> allComfortPieces;
        public static AccessTools.FieldRef<Player, GameObject> PlacementGhost;

        public static void Init()
        {
            allComfortPiecesField = AccessTools.Field(typeof(Piece), "s_allComfortPieces")
                ?? throw new MissingFieldException(nameof(Piece), "s_allComfortPieces");
            PlacementGhost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        }

        // Every loaded comfort piece (the game keeps this set itself; read only). Fetched on first use, in game.
        public static HashSet<Piece> AllComfortPieces =>
            allComfortPieces ?? (allComfortPieces = (HashSet<Piece>)allComfortPiecesField.GetValue(null));
    }

    // Finds comfort chains: comfort pieces closer than the link range to each other, each one linkable (under a roof
    // unless the setting is off). Works on the loaded pieces only, which covers any house you're standing in.
    internal static class Chain
    {
        public struct Link
        {
            public Vector3 From;
            public Vector3 To;
        }

        private const float RoofRecheck = 10f;   // seconds a roof check is trusted (pieces don't move; roofs rarely change)
        private const int MaxPieces = 3000;      // safety cap on one chain

        private struct RoofState
        {
            public bool Roofed;
            public float Until;
        }

        private static readonly Dictionary<Piece, RoofState> roofs = new Dictionary<Piece, RoofState>();
        private static readonly Dictionary<Piece, Vector3> anchors = new Dictionary<Piece, Vector3>();
        private static float nextPurge;
        private static int roofMask;
        private static int ghostLayer = -1;
        private static readonly RaycastHit[] hits = new RaycastHit[32];

        // Scratch for one search.
        private static readonly List<Piece> nodes = new List<Piece>();
        private static readonly Dictionary<Vector3Int, List<int>> cells = new Dictionary<Vector3Int, List<int>>();
        private static readonly Stack<List<int>> spareCells = new Stack<List<int>>();
        private static readonly Queue<int> queue = new Queue<int>();
        private static readonly HashSet<Piece> already = new HashSet<Piece>();
        private static int[] parent = new int[0];   // -1 = not in the chain, -2 = a seed, -3 = linked to the ghost
        private static float cellSize;

        // The comfort check: every piece in the chains that reach within `radius` of `center` goes into `result`
        // (the game's own list of pieces within 10 m), once.
        public static void AddChain(Vector3 center, float radius, List<Piece> result)
        {
            Snapshot();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (Vector3.Distance(center, nodes[i].transform.position) < radius && Linkable(nodes[i]))
                {
                    parent[i] = -2;
                    queue.Enqueue(i);
                }
            }
            Spread();

            already.Clear();
            already.UnionWith(result);
            for (int i = 0; i < nodes.Count; i++)
            {
                if (parent[i] != -1 && already.Add(nodes[i]))
                {
                    result.Add(nodes[i]);
                }
            }
        }

        // The dots: the links of the chain `focus` belongs to, as a tree grown from it, each link pointing back toward
        // `focus`. For a placement ghost, the chain it would join.
        public static void Links(Piece focus, bool ghost, List<Link> links)
        {
            links.Clear();
            Snapshot();
            Vector3 focusAnchor = ghost ? GhostAnchor(focus) : Anchor(focus);
            if (ghost)
            {
                if (Settings.RoofedOnly.Value && !CheckRoof(focus, focusAnchor))
                {
                    return;
                }
                Vector3 position = focus.transform.position;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (Vector3.Distance(position, nodes[i].transform.position) < Settings.LinkRange.Value && Linkable(nodes[i]))
                    {
                        parent[i] = -3;
                        queue.Enqueue(i);
                    }
                }
            }
            else
            {
                int start = nodes.IndexOf(focus);
                if (start < 0 || !Linkable(focus))
                {
                    return;
                }
                parent[start] = -2;
                queue.Enqueue(start);
            }
            Spread();

            for (int i = 0; i < nodes.Count; i++)
            {
                if (parent[i] >= 0)
                {
                    links.Add(new Link { From = Anchor(nodes[i]), To = Anchor(nodes[parent[i]]) });
                }
                else if (parent[i] == -3)
                {
                    links.Add(new Link { From = Anchor(nodes[i]), To = focusAnchor });
                }
            }
        }

        // Every placed comfort piece, bucketed into cells one link range wide, all marked "not in the chain".
        private static void Snapshot()
        {
            if (ghostLayer < 0)
            {
                ghostLayer = LayerMask.NameToLayer("ghost");
                roofMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
            }
            PurgeCaches();

            nodes.Clear();
            queue.Clear();
            foreach (List<int> list in cells.Values)
            {
                list.Clear();
                spareCells.Push(list);
            }
            cells.Clear();
            cellSize = Mathf.Max(1f, Settings.LinkRange.Value);

            foreach (Piece piece in Access.AllComfortPieces)
            {
                if (piece && piece.gameObject.layer != ghostLayer)
                {
                    nodes.Add(piece);
                }
            }
            if (parent.Length < nodes.Count)
            {
                parent = new int[nodes.Count * 2];
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                parent[i] = -1;
                Vector3Int cell = CellOf(nodes[i].transform.position);
                if (!cells.TryGetValue(cell, out List<int> list))
                {
                    list = spareCells.Count > 0 ? spareCells.Pop() : new List<int>();
                    cells[cell] = list;
                }
                list.Add(i);
            }
        }

        // Breadth-first from the queued pieces: any linkable piece closer than the link range to a chain piece joins.
        private static void Spread()
        {
            float range = Settings.LinkRange.Value;
            int count = queue.Count;
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                Vector3 position = nodes[i].transform.position;
                Vector3Int cell = CellOf(position);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!cells.TryGetValue(new Vector3Int(cell.x + dx, cell.y + dy, cell.z + dz), out List<int> list))
                    {
                        continue;
                    }
                    foreach (int j in list)
                    {
                        if (parent[j] != -1 || Vector3.Distance(position, nodes[j].transform.position) >= range || !Linkable(nodes[j]))
                        {
                            continue;
                        }
                        parent[j] = i;
                        queue.Enqueue(j);
                        if (++count >= MaxPieces)
                        {
                            queue.Clear();
                            return;
                        }
                    }
                }
            }
        }

        private static Vector3Int CellOf(Vector3 position) =>
            new Vector3Int(Mathf.FloorToInt(position.x / cellSize), Mathf.FloorToInt(position.y / cellSize),
                Mathf.FloorToInt(position.z / cellSize));

        private static bool Linkable(Piece piece)
        {
            if (!Settings.RoofedOnly.Value)
            {
                return true;
            }
            float now = Time.time;
            if (roofs.TryGetValue(piece, out RoofState state) && now < state.Until)
            {
                return state.Roofed;
            }
            bool roofed = CheckRoof(piece, Anchor(piece));
            // Spread the rechecks out so a big house doesn't recheck every piece in the same frame.
            roofs[piece] = new RoofState { Roofed = roofed, Until = now + RoofRecheck + UnityEngine.Random.Range(0f, 3f) };
            return roofed;
        }

        // The game's own roof test (Cover.IsUnderRoof: a thin sphere straight up, "leaky" pieces don't count), except
        // that the piece's own parts don't count either: a bed canopy, a chair back or a hearth's hood isn't a roof.
        private static bool CheckRoof(Piece piece, Vector3 from)
        {
            int count = Physics.SphereCastNonAlloc(from, 0.1f, Vector3.up, hits, 100f, roofMask);
            for (int i = 0; i < count; i++)
            {
                Collider collider = hits[i].collider;
                if (!collider || collider.CompareTag("leaky") || collider.GetComponentInParent<Piece>() == piece)
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        // Where links start and end, and where the roof test starts: the middle of the piece's solid colliders (a rug
        // just above the floor, a bed at mattress height, a banner halfway up). Placed pieces don't move, so it's cached.
        private static Vector3 Anchor(Piece piece)
        {
            if (!anchors.TryGetValue(piece, out Vector3 anchor))
            {
                anchor = Middle(piece);
                anchors[piece] = anchor;
            }
            return anchor;
        }

        private static Vector3 GhostAnchor(Piece ghost) => Middle(ghost);

        private static Vector3 Middle(Piece piece)
        {
            bool found = false;
            Bounds bounds = default;
            foreach (Collider collider in piece.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger)
                {
                    continue;
                }
                if (found)
                {
                    bounds.Encapsulate(collider.bounds);
                }
                else
                {
                    bounds = collider.bounds;
                    found = true;
                }
            }
            return found ? bounds.center : piece.transform.position + Vector3.up * 0.5f;
        }

        // Forget removed pieces now and then.
        private static void PurgeCaches()
        {
            if (Time.time < nextPurge)
            {
                return;
            }
            nextPurge = Time.time + 60f;
            Purge(roofs);
            Purge(anchors);
        }

        private static readonly List<Piece> dead = new List<Piece>();

        private static void Purge<T>(Dictionary<Piece, T> cache)
        {
            dead.Clear();
            foreach (Piece piece in cache.Keys)
            {
                if (!piece)
                {
                    dead.Add(piece);
                }
            }
            foreach (Piece piece in dead)
            {
                cache.Remove(piece);
            }
        }
    }

    // The game counts comfort in SE_Rested.CalculateComfortLevel(inShelter, position): under a roof it asks
    // Piece.GetAllComfortPiecesInRadius for the pieces within 10 m, then takes the best of each comfort group. While
    // it does, that list gets the rest of their chains added. (GetAllComfortPiecesInRadius is hooked rather than the
    // tiny SE_Rested.GetNearbyComfortPieces, which the JIT may inline.)
    internal static class ComfortPatches
    {
        private static bool counting;
        private static Vector3 center;
        private static bool failed;

        [HarmonyPatch(typeof(SE_Rested), nameof(SE_Rested.CalculateComfortLevel), typeof(bool), typeof(Vector3))]
        private static class SE_Rested_CalculateComfortLevel
        {
            private static void Prefix(Vector3 position)
            {
                counting = Settings.Enabled.Value && !failed;
                center = position;
            }

            private static void Finalizer()
            {
                counting = false;
            }
        }

        [HarmonyPatch(typeof(Piece), nameof(Piece.GetAllComfortPiecesInRadius))]
        private static class Piece_GetAllComfortPiecesInRadius
        {
            private static void Postfix(float radius, List<Piece> pieces)
            {
                if (!counting)
                {
                    return;
                }
                counting = false;
                try
                {
                    Chain.AddChain(center, radius, pieces);
                }
                catch (Exception e)
                {
                    // Comfort falls back to vanilla for the rest of the session rather than failing every 2 seconds.
                    failed = true;
                    Plugin.Log.LogError($"Comfort chains failed; comfort is vanilla until the game restarts. {e}");
                }
            }
        }
    }
}
