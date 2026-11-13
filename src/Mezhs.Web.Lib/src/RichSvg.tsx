import { createElement } from "react";
import type { ReactNode } from "react";

// React element names are selected exclusively from this host-owned allowlist.
// No SVG DOM parsing, HTML injection, SVG assets, events or URL references.
const svgTags = ["svg", "g", "rect", "path", "line", "polyline", "polygon", "circle", "ellipse", "text", "tspan"] as const;
type SvgTag = typeof svgTags[number];

const reactProps: Record<string, string> = {
  viewBox: "viewBox", preserveAspectRatio: "preserveAspectRatio", role: "role",
  "aria-label": "aria-label", strokeWidth: "strokeWidth", strokeLinecap: "strokeLinecap",
  strokeLinejoin: "strokeLinejoin", strokeDasharray: "strokeDasharray",
  fillOpacity: "fillOpacity", strokeOpacity: "strokeOpacity",
  fontSize: "fontSize", fontFamily: "fontFamily", fontWeight: "fontWeight",
  textAnchor: "textAnchor", dominantBaseline: "dominantBaseline",
};

// C# RichSvgValidation owns syntax, geometry, nesting and size limits.
// This fixed bridge only converts safe typed DTO attributes to React properties.
const supported = [
  "viewBox", "preserveAspectRatio", "role", "aria-label", "d", "points",
  "fill", "stroke", "strokeWidth", "strokeLinecap", "strokeLinejoin",
  "strokeDasharray", "opacity", "fillOpacity", "strokeOpacity", "transform",
  "x", "y", "x1", "x2", "y1", "y2", "cx", "cy", "r", "rx", "ry",
  "width", "height", "fontSize", "fontFamily", "fontWeight", "textAnchor",
  "dominantBaseline",
] as const;

export function svgComponents() {
  const components: Record<string, (props: Record<string, unknown>) => ReactNode> = {};
  for (const tag of svgTags) {
    components["mezhs-svg-" + tag] = (input: Record<string, unknown>) => {
      const props: Record<string, unknown> = {};
      for (const name of supported) {
        const value = input["data-mezhs-svg-" + name.toLowerCase()];
        if (typeof value === "string") props[reactProps[name] || name] = value;
      }
      if (tag === "svg") {
        props.role = "img";
        props["aria-label"] = props["aria-label"] || "Model diagram";
        props.className = "rich-svg";
        props.width = "100%";
      }
      return createElement(tag, props, input.children as ReactNode);
    };
  }
  return components;
}
