import { createElement } from "react";
import type { ReactNode } from "react";

// A typed C# document cannot name arbitrary DOM/SVG elements.
const svgTags = new Set([
  "svg", "g", "rect", "path", "line", "polyline", "polygon", "circle", "ellipse", "text", "tspan",
]);

// Defense-in-depth at the DOM sink; C# RichSvgValidation independently owns
// SVG syntax, geometry, nesting and resource limits. No event, URL or CSS props.
const safeSvgProps = new Set([
  "viewBox", "preserveAspectRatio", "role", "aria-label", "d", "points",
  "fill", "stroke", "strokeWidth", "strokeLinecap", "strokeLinejoin",
  "strokeDasharray", "opacity", "fillOpacity", "strokeOpacity", "transform",
  "x", "y", "x1", "x2", "y1", "y2", "cx", "cy", "r", "rx", "ry",
  "width", "height", "fontSize", "fontFamily", "fontWeight", "textAnchor",
  "dominantBaseline",
]);

export function renderSvg(tag: string, attributes: Record<string, string>, children: ReactNode): ReactNode {
  if (!svgTags.has(tag)) return null;
  const props: Record<string, string> = {};
  for (const [name, value] of Object.entries(attributes)) {
    if (safeSvgProps.has(name) && typeof value === "string") props[name] = value;
  }
  if (tag === "svg") {
    props.role = "img";
    props["aria-label"] = props["aria-label"] || "Model diagram";
    props.className = "rich-svg";
    props.width = "100%";
  }
  return createElement(tag, props, children);
}
