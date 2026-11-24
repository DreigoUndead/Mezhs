import { createElement } from "react";
import { Cite } from "./Cite";
import { RichCodeBlock } from "./CodeBlock";
import { RichWritingBlock } from "./WritingBlock";
import { rowComponent } from "./Row";
import { colComponent } from "./Col";
import { boxComponent } from "./Box";
import { gridComponent } from "./Grid";
import { gridItemComponent } from "./GridItem";
import { textComponent } from "./Text";
import { titleComponent } from "./Title";
import { captionComponent } from "./Caption";
import { dividerComponent } from "./Divider";
import { badgeComponent } from "./Badge";
import { iconComponent } from "./Icon";
import { linkComponent } from "./Link";
import { chartComponent } from "./Chart";

// Only server-validated symbolic names select these statically imported
// host-owned components; model content is never an import path or component.
// Only symbolic server-validated tag names map to host-owned presentation.
export const presentation = {
  row: rowComponent,
  col: colComponent,
  box: boxComponent,
  grid: gridComponent,
"grid-item": gridItemComponent,
  text: textComponent,
  title: titleComponent,
  caption: captionComponent,
  divider: dividerComponent,
  badge: badgeComponent,
  icon: iconComponent,
  Link: linkComponent,
  Chart: chartComponent,
  Cite,
};

// Raw editor markup is inert data; React only invokes these trusted factories.
export const rawBlocks = {
  CodeBlock: (source: string, attrs: Record<string, string>) =>
    createElement(RichCodeBlock, {
      source,
      language: /^[\\w+#.-]{1,32}$/.test(attrs.language || "") ? attrs.language : "text",
      editable: attrs.editable === "true",
    }),
  WritingBlock: (source: string, attrs: Record<string, string>) =>
    createElement(RichWritingBlock, {
      source,
      variant: attrs.variant || "standard",
      subject: attrs.subject || "",
      recipient: attrs.recipient || "",
      cc: attrs.cc || "",
      bcc: attrs.bcc || "",
    }),
};

