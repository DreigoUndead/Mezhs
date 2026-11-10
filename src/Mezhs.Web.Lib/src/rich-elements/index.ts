import { rowElement } from "./Row";
import { colElement } from "./Col";
import { boxElement } from "./Box";
import { gridElement } from "./Grid";
import { gridItemElement } from "./GridItem";
import { textElement } from "./Text";
import { titleElement } from "./Title";
import { captionElement } from "./Caption";
import { dividerElement } from "./Divider";
import { badgeElement } from "./Badge";
import { iconElement } from "./Icon";
import { linkElement } from "./Link";
import { chartElement } from "./Chart";
import type { RichElement } from "./Element";
const all: RichElement[] = [
rowElement, colElement, boxElement, gridElement, gridItemElement, textElement, titleElement, captionElement, dividerElement, badgeElement, iconElement, linkElement, chartElement];
export const richElementRegistry: Record<string, RichElement> =
  Object.fromEntries(all.map(element => [element.tag, element]));
export const richStaticComponents = Object.fromEntries(all.map(element =>
  ["mezhs-" + element.tag.toLowerCase(), element.component]));
