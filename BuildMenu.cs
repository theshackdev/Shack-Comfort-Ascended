using HarmonyLib;
using TMPro;
using UnityEngine;

namespace TheShack.ComfortAscended
{
    // The hammer's build menu shows which pieces give comfort and how much: a "+N" in the corner of the piece's icon, and
    // a line under its description naming its comfort group, since only the best piece of a group counts.
    internal static class BuildMenu
    {
        private const string BadgeName = "ShackComfortBadge";
        private static readonly Color BadgeColor = new Color(1f, 0.85f, 0.35f);

        // Every icon in the menu goes through BuildUiPieceButton.Setup, SearsCatalog's pooled grid included (it reuses
        // buttons for other pieces), so the badge is set or hidden there each time.
        [HarmonyPatch(typeof(BuildUiPieceButton), nameof(BuildUiPieceButton.Setup))]
        private static class BuildUiPieceButton_Setup
        {
            private static void Postfix(BuildUiPieceButton __instance, Piece pieceInfo)
            {
                Transform badge = __instance.transform.Find(BadgeName);
                if (!Settings.ShowInBuildMenu.Value || !pieceInfo || pieceInfo.m_comfort <= 0)
                {
                    if (badge)
                    {
                        badge.gameObject.SetActive(false);
                    }
                    return;
                }
                TMP_Text text = badge ? badge.GetComponent<TMP_Text>() : CreateBadge(__instance.transform);
                if (text)
                {
                    text.text = "+" + pieceInfo.m_comfort;
                    text.gameObject.SetActive(true);
                }
            }
        }

        // The hovered or selected piece's name, description and costs, redrawn by the game every frame.
        [HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
        private static class Hud_SetupPieceInfo
        {
            private static void Postfix(Hud __instance, Piece piece)
            {
                if (Settings.ShowInBuildMenu.Value && piece && piece.m_comfort > 0 && __instance.m_pieceDescription)
                {
                    __instance.m_pieceDescription.text += "\n" + Line(piece);
                }
            }
        }

        private static string Line(Piece piece)
        {
            string lit = piece.m_comfortObject ? ", while lit" : "";
            string group = piece.m_comfortGroup == Piece.ComfortGroup.None
                ? "each different one counts"
                : $"only your best {GroupName(piece.m_comfortGroup)} counts";
            return $"<color=#FFD966>Comfort +{piece.m_comfort}</color> <color=#C8C8C8>({group}{lit})</color>";
        }

        private static string GroupName(Piece.ComfortGroup group)
        {
            switch (group)
            {
                case Piece.ComfortGroup.Chair: return "seat";
                case Piece.ComfortGroup.Carpet: return "rug";
                case Piece.ComfortGroup.Decor: return "decoration";
                case Piece.ComfortGroup.Leisure: return "leisure piece";
                default: return group.ToString().ToLowerInvariant();
            }
        }

        // A small label in the icon's lower-left corner, in the build HUD's own font. It never takes clicks.
        private static TMP_Text CreateBadge(Transform button)
        {
            TMP_Text model = Hud.instance ? Hud.instance.m_pieceDescription : null;
            if (!model)
            {
                return null;
            }
            GameObject go = new GameObject(BadgeName, typeof(RectTransform));
            go.transform.SetParent(button, false);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = Vector2.zero;
            rect.offsetMin = new Vector2(4f, 2f);
            rect.offsetMax = new Vector2(-4f, 0f);
            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.font = model.font;
            text.fontSharedMaterial = model.fontSharedMaterial;
            text.enableAutoSizing = true;
            text.fontSizeMin = 8f;
            text.fontSizeMax = 18f;
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.BottomLeft;
            text.overflowMode = TextOverflowModes.Overflow;
            text.color = BadgeColor;
            text.raycastTarget = false;
            go.transform.SetAsLastSibling();
            return text;
        }
    }
}
