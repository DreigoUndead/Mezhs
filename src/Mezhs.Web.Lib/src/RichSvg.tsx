import { createElement } from "react";
import type { ReactNode } from "react";

// React element names are selected exclusively from this host-owned allowlist.
// No SVG DOM parsing, HTML injection, SVG assets, events or URL references.
const svgTags = ["svg", "g", "rect", "path", "line", "polyline", "polygon", "circle", "ellipse", "text", "tspan"] as const;
type SvgTag = typeof svgTags[number];
const tags = new Set<string>(svgTags);
const drawing = new Set(["fill", "stroke", "strokeWidth", "strokeLinecap", "strokeLinejoin",
  "strokeDasharray", "opacity", "fillOpacity", "strokeOpacity", "transform"]);
const geometry = new Set(["x", "y", "x1", "x2", "y1", "y2", "cx", "cy", "r", "rx", "ry", "width", "height"]);
const typography = new Set(["fontSize", "fontFamily", "fontWeight", "textAnchor", "dominantBaseline"]);
const allowed = new Set(["viewBox", "preserveAspectRatio", "role", "aria-label", "d", "points",
  ...drawing, ...geometry, ...typography]);
const numeric = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i;
const numbers = /[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?/gi;
const coordinates = /^[\d+.,\s\-eE]+$/;
const paths = /^[MmLlHhVvCcSsQqTtAaZz\d+.,\s\-eE]+$/;
const paints = /^(?:none|currentColor|transparent|#[0-9a-fA-F]{3,8}|[a-zA-Z]{1,24}|rgba?\([\d.,%\s]+\))$/;

export type SvgMarkupNode = {
  type: string;
  name?: string | null;
  attributes?: Array<{ type: string; name?: string; value?: string | { value: string } | null }>;
  children?: SvgMarkupNode[];
  data?: { hName?: string; hProperties?: Record<string, string> };
  value?: string;
};

const reactProps: Record<string, string> = {
  viewBox: "viewBox", preserveAspectRatio: "preserveAspectRatio", role: "role",
  "aria-label": "aria-label", strokeWidth: "strokeWidth", strokeLinecap: "strokeLinecap",
  strokeLinejoin: "strokeLinejoin", strokeDasharray: "strokeDasharray",
  fillOpacity: "fillOpacity", strokeOpacity: "strokeOpacity",
  fontSize: "fontSize", fontFamily: "fontFamily", fontWeight: "fontWeight",
  textAnchor: "textAnchor", dominantBaseline: "dominantBaseline",
};

function validNumbers(value: string, min: number, max: number, count?: number) {
  const pieces = value.trim().split(/[\s,]+/);
  return (!count || pieces.length === count) && pieces.length <= 200 &&
    pieces.every(piece => numeric.test(piece) && Number.isFinite(Number(piece)) &&
      Number(piece) >= min && Number(piece) <= max);
}

function valid(name: string, value: string, tag: string) {
  if (value.length > 2048 || /url\s*\(|(?:javascript|data)\s*:|[<>\\;]/i.test(value)) return false;
  if (["role", "aria-label", "viewBox", "preserveAspectRatio"].includes(name) && tag !== "svg") return false;
  if (name === "role") return value === "img";
  if (name === "aria-label") return value.trim().length > 0 && value.length <= 200;
  if (name === "viewBox") return validNumbers(value, -10000, 10000, 4) &&
    value.trim().split(/[\s,]+/).slice(2).every(n => Number(n) > 0);
  if (name === "preserveAspectRatio") return /^(?:none|x(?:Min|Mid|Max)Y(?:Min|Mid|Max) (?:meet|slice))$/.test(value);
  if (name === "d") return tag === "path" && paths.test(value) &&
    [...value.matchAll(numbers)].every(m => Math.abs(Number(m[0])) <= 10000);
  if (name === "points") return ["polygon", "polyline"].includes(tag) &&
    coordinates.test(value) && validNumbers(value, -10000, 10000);
  if (["fill", "stroke"].includes(name)) return paints.test(value);
  if (["strokeLinecap"].includes(name)) return /^(round|square|butt)$/.test(value);
  if (name === "strokeLinejoin") return /^(round|bevel|miter)$/.test(value);
  if (name === "strokeDasharray") return validNumbers(value, 0, 200);
  if (name === "textAnchor") return /^(start|middle|end)$/.test(value);
  if (name === "dominantBaseline") return /^(auto|middle|central|hanging|alphabetic)$/.test(value);
  if (name === "fontFamily") return /^[\w ,'-]{1,64}$/.test(value);
  if (name === "fontWeight") return /^(normal|bold|[1-9]00)$/.test(value);
  if (name === "transform") return /^(?:(?:translate|scale|rotate)\([\d+.,\s\-]+\)\s*){1,3}$/.test(value) &&
    [...value.matchAll(numbers)].every(m => Math.abs(Number(m[0])) <= 10000);
  if (name === "width" && tag === "svg" && value === "100%") return true;
  if (name === "height" && tag === "svg") return validNumbers(value, 1, 800, 1);
  if (name === "width" && tag === "svg") return validNumbers(value, 1, 1600, 1);
  if (["opacity", "fillOpacity", "strokeOpacity"].includes(name)) return validNumbers(value, 0, 1, 1);
  if (name === "fontSize") return validNumbers(value, 1, 80, 1);
  if (name === "strokeWidth") return validNumbers(value, 0, 30, 1);
  if (geometry.has(name)) return validNumbers(value, -10000, 10000, 1);
  return false;
}

// Validation is atomic. Only after every descendant passes do we annotate the
// tree; an invalid child cannot leave an unsafe partial SVG mounted.
export function prepareSvg(root: SvgMarkupNode): boolean {
  let count = 0;
  let length = 0;
  const changes: Array<{ node: SvgMarkupNode; tag: SvgTag; props: Record<string, string> }> = [];
  function inspect(node: SvgMarkupNode, depth: number): boolean {
    if (++count > 250 || depth > 12) return false;
    if (node.type === "text") {
      length += node.value?.length || 0;
      return length <= 24000;
    }
    if ((node.type !== "mdxJsxFlowElement" && node.type !== "mdxJsxTextElement") ||
        !node.name || !tags.has(node.name)) return false;
    const tag = node.name as SvgTag;
    const props: Record<string, string> = Object.create(null);
    for (const attr of node.attributes || []) {
      if (attr.type !== "mdxJsxAttribute" || !attr.name ||
          !allowed.has(attr.name) || Object.prototype.hasOwnProperty.call(props, attr.name)) return false;
      // Numeric JSX literals only. Everything else must be a static quoted string.
      const value = typeof attr.value === "string" ? attr.value :
        attr.value && typeof attr.value === "object" &&
        /^-?\d+(?:\.\d+)?$/.test(attr.value.value.trim()) ? attr.value.value.trim() : null;
      if (value === null || !valid(attr.name, value, tag)) return false;
      props[attr.name] = value;
      length += value.length;
      if (length > 24000) return false;
    }
    if (tag !== "svg" && ["width", "height"].some(n => n in props) &&
        !["rect", "ellipse"].includes(tag)) return false;
    changes.push({ node, tag, props });
    return (node.children || []).every(child => inspect(child, depth + 1));
  }
  if (root.name !== "svg" || !inspect(root, 0)) return false;
  for (const change of changes) {
    const attrs: Record<string, string> = {};
    for (const [key, value] of Object.entries(change.props))
      attrs["data-mezhs-svg-" + key.toLowerCase()] = value;
    change.node.data = { hName: "mezhs-svg-" + change.tag, hProperties: attrs };
  }
  return true;
}

export function svgComponents() {
  const components: Record<string, (props: Record<string, unknown>) => ReactNode> = {};
  for (const tag of svgTags) {
    components["mezhs-svg-" + tag] = (input: Record<string, unknown>) => {
      const props: Record<string, unknown> = {};
      for (const name of allowed) {
        const value = input["data-mezhs-svg-" + name.toLowerCase()];
        if (typeof value === "string" && valid(name, value, tag))
          props[reactProps[name] || name] = value;
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
