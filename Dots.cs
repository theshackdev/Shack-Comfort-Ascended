using System.Collections.Generic;
using UnityEngine;

namespace TheShack.ComfortAscended
{
    // Moving dots along a comfort chain's links, using the workbench extensions' own connection effect (the prefab
    // StationExtension.m_connectionPrefab: a line of dots along its local Z axis, one meter long until scaled). Shown
    // while the local player looks at a comfort piece (with the hammer out, any comfort piece; without it, ones that
    // have hover text: chairs, beds, fires...) or places one. The dots flow toward the piece you look at.
    internal static class Dots
    {
        private const float Refresh = 0.1f;   // how often the focus and its chain are looked up
        private const float Linger = 1f;      // the dots stay this long after you look away (vanilla hover: 1 s)
        private const int MaxLines = 150;

        private static GameObject prefab;
        private static bool prefabMissing;
        private static readonly List<GameObject> lines = new List<GameObject>();
        private static readonly List<Chain.Link> links = new List<Chain.Link>();
        private static float nextRefresh;
        private static float hideAt;

        public static void Update()
        {
            if (Time.time < nextRefresh)
            {
                return;
            }
            nextRefresh = Time.time + Refresh;

            Player player = Player.m_localPlayer;
            if (!player || !Settings.Enabled.Value || !Settings.ShowDots.Value || !Prefab())
            {
                Clear();
                return;
            }

            Piece focus = Focus(player, out bool ghost);
            if (focus)
            {
                Chain.Links(focus, ghost, links);
                if (links.Count == 0)
                {
                    Clear();
                    return;
                }
                Show();
                hideAt = Time.time + Linger;
            }
            else if (Time.time >= hideAt)
            {
                Clear();
            }
        }

        private static Piece Focus(Player player, out bool ghost)
        {
            ghost = false;
            if (player.InPlaceMode())
            {
                GameObject placing = Access.PlacementGhost(player);
                Piece piece = placing && placing.activeInHierarchy ? placing.GetComponent<Piece>() : null;
                if (piece && piece.m_comfort > 0)
                {
                    ghost = true;
                    return piece;
                }
                piece = player.GetHoveringPiece();
                if (piece && piece.m_comfort > 0)
                {
                    return piece;
                }
            }
            GameObject hovering = player.GetHoverObject();
            Piece hovered = hovering ? hovering.GetComponentInParent<Piece>() : null;
            return hovered && hovered.m_comfort > 0 ? hovered : null;
        }

        private static void Show()
        {
            int count = Mathf.Min(links.Count, MaxLines);
            for (int i = 0; i < count; i++)
            {
                if (i == lines.Count)
                {
                    lines.Add(Object.Instantiate(prefab));
                }
                else if (!lines[i])
                {
                    lines[i] = Object.Instantiate(prefab);
                }
                Place(lines[i], links[i].From, links[i].To);
            }
            for (int i = lines.Count - 1; i >= count; i--)
            {
                Destroy(lines[i]);
                lines.RemoveAt(i);
            }
        }

        // As StationExtension.StartConnectionEffect does it.
        private static void Place(GameObject line, Vector3 from, Vector3 to)
        {
            Vector3 direction = to - from;
            if (direction.sqrMagnitude < 0.01f)
            {
                line.SetActive(false);
                return;
            }
            line.SetActive(true);
            line.transform.position = from;
            line.transform.rotation = Quaternion.LookRotation(direction.normalized);
            line.transform.localScale = new Vector3(1f, 1f, direction.magnitude);
        }

        private static void Clear()
        {
            foreach (GameObject line in lines)
            {
                Destroy(line);
            }
            lines.Clear();
        }

        private static void Destroy(GameObject line)
        {
            if (line)
            {
                Object.Destroy(line);
            }
        }

        private static bool Prefab()
        {
            if (prefab)
            {
                return true;
            }
            if (prefabMissing || !ZNetScene.instance)
            {
                return false;
            }
            GameObject extension = ZNetScene.instance.GetPrefab("piece_workbench_ext1");
            StationExtension station = extension ? extension.GetComponent<StationExtension>() : null;
            prefab = station ? station.m_connectionPrefab : null;
            if (!prefab)
            {
                prefabMissing = true;
                Plugin.Log.LogWarning("The workbench extensions' connection effect wasn't found; comfort chains work but show no dots.");
            }
            return prefab;
        }
    }
}
