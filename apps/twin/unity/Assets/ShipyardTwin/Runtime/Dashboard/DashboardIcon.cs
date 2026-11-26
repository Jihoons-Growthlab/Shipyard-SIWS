using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Vector icon for the dashboard (no font glyphs needed). Drawn on a 24×24 grid with the element's text colour,
    /// so USS <c>color</c> (hover, active) tints it.
    /// </summary>
    [UxmlElement]
    public partial class DashboardIcon : VisualElement
    {
        /// <summary>Glyphs this icon draws; anything else draws the mark.</summary>
        public static readonly string[] Glyphs =
        {
            "mark", "crane", "calendar", "layers", "help", "depth", "chat", "power", "close",
            "search", "block", "hull", "vehicle", "facility", "building", "walk", "map", "warning",
        };

        /// <summary>Glyph for a kind of yard object (search result cards).</summary>
        public static string GlyphFor(TargetCategory category) => category switch
        {
            TargetCategory.Hull => "hull",
            TargetCategory.Block => "block",
            TargetCategory.Crane => "crane",
            TargetCategory.Vehicle => "vehicle",
            TargetCategory.Facility => "facility",
            TargetCategory.Prop => "building",
            _ => "search",
        };

        private string _glyph = "mark";

        public DashboardIcon()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(_ => MarkDirtyRepaint());
        }

        [UxmlAttribute]
        public string Glyph
        {
            get => _glyph;
            set
            {
                _glyph = value;
                MarkDirtyRepaint();
            }
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            var s = Mathf.Min(rect.width, rect.height) / 24f;
            var o = new Vector2(rect.x + (rect.width - 24f * s) / 2f, rect.y + (rect.height - 24f * s) / 2f);
            Vector2 P(float x, float y) => o + new Vector2(x, y) * s;

            var p = context.painter2D;
            p.strokeColor = resolvedStyle.color;
            p.fillColor = resolvedStyle.color;
            p.lineWidth = 1.8f * s;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            switch (_glyph)
            {
                case "crane":
                    p.BeginPath();
                    p.MoveTo(P(6, 21)); p.LineTo(P(6, 4)); p.LineTo(P(21, 4));    // mast and jib
                    p.MoveTo(P(6, 8)); p.LineTo(P(10, 4));                         // brace
                    p.MoveTo(P(3, 21)); p.LineTo(P(10, 21));                       // base
                    p.MoveTo(P(17, 4)); p.LineTo(P(17, 11));                       // hoist rope
                    p.Stroke();
                    p.BeginPath();
                    p.MoveTo(P(14, 11)); p.LineTo(P(20, 11)); p.LineTo(P(20, 16)); p.LineTo(P(14, 16)); p.ClosePath();   // block
                    p.Stroke();
                    break;
                case "calendar":
                    p.BeginPath();
                    p.MoveTo(P(4, 6)); p.LineTo(P(20, 6)); p.LineTo(P(20, 20)); p.LineTo(P(4, 20)); p.ClosePath();
                    p.MoveTo(P(4, 10)); p.LineTo(P(20, 10));
                    p.MoveTo(P(8, 4)); p.LineTo(P(8, 7));
                    p.MoveTo(P(16, 4)); p.LineTo(P(16, 7));
                    p.MoveTo(P(8, 15)); p.LineTo(P(16, 15));
                    p.Stroke();
                    break;
                case "layers":
                    p.BeginPath();
                    p.MoveTo(P(3, 8)); p.LineTo(P(12, 3)); p.LineTo(P(21, 8)); p.LineTo(P(12, 13)); p.ClosePath();
                    p.MoveTo(P(3, 12)); p.LineTo(P(12, 17)); p.LineTo(P(21, 12));
                    p.MoveTo(P(3, 16)); p.LineTo(P(12, 21)); p.LineTo(P(21, 16));
                    p.Stroke();
                    break;
                case "help":
                    p.BeginPath();
                    p.Arc(P(12, 12), 9f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.MoveTo(P(9, 9)); p.BezierCurveTo(P(9, 5), P(16, 5), P(15, 10));
                    p.BezierCurveTo(P(15, 12), P(12, 12), P(12, 14));
                    p.Stroke();
                    p.BeginPath();
                    p.Arc(P(12, 17), 1f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.Fill();
                    break;
                case "depth":
                    p.BeginPath();
                    for (var i = 0; i < 3; i++)
                    {
                        var y = 7f + i * 5f;
                        p.MoveTo(P(3, y));
                        p.BezierCurveTo(P(6, y - 3), P(9, y + 3), P(12, y));
                        p.BezierCurveTo(P(15, y - 3), P(18, y + 3), P(21, y));
                    }

                    p.Stroke();
                    break;
                case "chat":
                    p.BeginPath();
                    p.MoveTo(P(6, 4)); p.LineTo(P(18, 4));
                    p.ArcTo(P(21, 4), P(21, 7), 3f * s);
                    p.LineTo(P(21, 13));
                    p.ArcTo(P(21, 16), P(18, 16), 3f * s);
                    p.LineTo(P(11, 16)); p.LineTo(P(6, 20)); p.LineTo(P(7, 16)); p.LineTo(P(6, 16));
                    p.ArcTo(P(3, 16), P(3, 13), 3f * s);
                    p.LineTo(P(3, 7));
                    p.ArcTo(P(3, 4), P(6, 4), 3f * s);
                    p.ClosePath();
                    p.Stroke();
                    for (var i = 0; i < 3; i++)
                    {
                        p.BeginPath();
                        p.Arc(P(8 + i * 4, 10), 1.1f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                        p.Fill();
                    }

                    break;
                case "power":
                    p.BeginPath();
                    p.Arc(P(12, 13), 7.5f * s, Angle.Degrees(-50f), Angle.Degrees(230f));
                    p.Stroke();
                    p.BeginPath();
                    p.MoveTo(P(12, 3)); p.LineTo(P(12, 12));
                    p.Stroke();
                    break;
                case "close":
                    p.BeginPath();
                    p.MoveTo(P(6, 6)); p.LineTo(P(18, 18));
                    p.MoveTo(P(18, 6)); p.LineTo(P(6, 18));
                    p.Stroke();
                    break;
                case "search":   // magnifier
                    p.BeginPath();
                    p.Arc(P(10.5f, 10.5f), 6.5f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.Stroke();
                    p.BeginPath();
                    p.MoveTo(P(15.3f, 15.3f)); p.LineTo(P(20.5f, 20.5f));
                    p.Stroke();
                    break;
                case "block":   // hull block: isometric box
                    p.BeginPath();
                    p.MoveTo(P(12, 3)); p.LineTo(P(20, 7.5f)); p.LineTo(P(20, 16.5f)); p.LineTo(P(12, 21));
                    p.LineTo(P(4, 16.5f)); p.LineTo(P(4, 7.5f)); p.ClosePath();
                    p.MoveTo(P(4, 7.5f)); p.LineTo(P(12, 12)); p.LineTo(P(20, 7.5f));
                    p.MoveTo(P(12, 12)); p.LineTo(P(12, 21));
                    p.Stroke();
                    break;
                case "hull":   // ship side: hull, deckhouse, waterline
                    p.BeginPath();
                    p.MoveTo(P(2, 12)); p.LineTo(P(22, 12)); p.LineTo(P(19, 17)); p.LineTo(P(5, 17)); p.ClosePath();
                    p.MoveTo(P(13, 12)); p.LineTo(P(13, 7)); p.LineTo(P(18, 7)); p.LineTo(P(18, 12));
                    p.MoveTo(P(15.5f, 7)); p.LineTo(P(15.5f, 4));
                    p.MoveTo(P(3, 20.5f)); p.BezierCurveTo(P(6, 19), P(9, 22), P(12, 20.5f));
                    p.BezierCurveTo(P(15, 19), P(18, 22), P(21, 20.5f));
                    p.Stroke();
                    break;
                case "vehicle":   // transporter / forklift: flat bed on wheels with a cab
                    p.BeginPath();
                    p.MoveTo(P(2, 15)); p.LineTo(P(22, 15)); p.LineTo(P(22, 11)); p.LineTo(P(2, 11)); p.ClosePath();
                    p.MoveTo(P(15, 11)); p.LineTo(P(15, 6)); p.LineTo(P(20, 6)); p.LineTo(P(22, 9)); p.LineTo(P(22, 11));
                    p.Stroke();
                    for (var i = 0; i < 3; i++)
                    {
                        p.BeginPath();
                        p.Arc(P(6 + i * 6, 18), 2f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                        p.Stroke();
                    }

                    break;
                case "facility":   // dock or quay seen from above: basin with a gate and bollards
                    p.BeginPath();
                    p.MoveTo(P(3, 5)); p.LineTo(P(21, 5)); p.LineTo(P(21, 19)); p.LineTo(P(3, 19)); p.ClosePath();
                    p.MoveTo(P(7, 9)); p.LineTo(P(21, 9));
                    p.MoveTo(P(7, 15)); p.LineTo(P(21, 15));
                    p.MoveTo(P(7, 9)); p.LineTo(P(7, 15));
                    p.Stroke();
                    p.BeginPath();
                    p.Arc(P(10, 12), 1.1f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.Arc(P(14.5f, 12), 1.1f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.Fill();
                    break;
                case "building":   // shop or warehouse: saw-tooth roof
                    p.BeginPath();
                    p.MoveTo(P(3, 20)); p.LineTo(P(3, 10)); p.LineTo(P(9, 6)); p.LineTo(P(9, 10)); p.LineTo(P(15, 6));
                    p.LineTo(P(15, 10)); p.LineTo(P(21, 6)); p.LineTo(P(21, 20)); p.ClosePath();
                    p.MoveTo(P(10, 20)); p.LineTo(P(10, 15)); p.LineTo(P(14, 15)); p.LineTo(P(14, 20));
                    p.Stroke();
                    break;
                case "walk":   // person walking (third-person walk mode)
                    p.BeginPath();
                    p.Arc(P(13, 4.5f), 2f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.Fill();
                    p.BeginPath();
                    p.MoveTo(P(12.5f, 8)); p.LineTo(P(11, 14));                     // body
                    p.MoveTo(P(7.5f, 12)); p.LineTo(P(10, 9)); p.LineTo(P(13, 9.5f)); p.LineTo(P(16, 12.5f));   // arms
                    p.MoveTo(P(11, 14)); p.LineTo(P(8, 21));                        // back leg
                    p.MoveTo(P(11, 14)); p.LineTo(P(14, 17)); p.LineTo(P(15.5f, 21));   // front leg
                    p.Stroke();
                    break;
                case "warning":   // hazard triangle with an exclamation mark
                    p.BeginPath();
                    p.MoveTo(P(12, 3)); p.LineTo(P(22, 20.5f)); p.LineTo(P(2, 20.5f)); p.ClosePath();
                    p.MoveTo(P(12, 9)); p.LineTo(P(12, 14.5f));
                    p.Stroke();
                    p.BeginPath();
                    p.Arc(P(12, 17.5f), 1.1f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.Fill();
                    break;
                case "map":   // folded map (back to the map view)
                    p.BeginPath();
                    p.MoveTo(P(3, 6)); p.LineTo(P(9, 4)); p.LineTo(P(15, 6)); p.LineTo(P(21, 4));
                    p.LineTo(P(21, 18)); p.LineTo(P(15, 20)); p.LineTo(P(9, 18)); p.LineTo(P(3, 20)); p.ClosePath();
                    p.MoveTo(P(9, 4)); p.LineTo(P(9, 18));
                    p.MoveTo(P(15, 6)); p.LineTo(P(15, 20));
                    p.Stroke();
                    break;
                default:   // mark: hull plan in a diamond
                    p.BeginPath();
                    p.MoveTo(P(12, 2)); p.LineTo(P(22, 12)); p.LineTo(P(12, 22)); p.LineTo(P(2, 12)); p.ClosePath();
                    p.Stroke();
                    p.BeginPath();
                    p.MoveTo(P(12, 7)); p.LineTo(P(15, 11)); p.LineTo(P(15, 17)); p.LineTo(P(9, 17)); p.LineTo(P(9, 11)); p.ClosePath();
                    p.Fill();
                    break;
            }
        }
    }
}
