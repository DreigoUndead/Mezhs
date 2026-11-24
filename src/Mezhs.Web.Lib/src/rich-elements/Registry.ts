import { rowComponent } from "./rich-elements/Row";
import { colComponent } from "./rich-elements/Col";
import { boxComponent } from "./rich-elements/Box";
import { gridComponent } from "./rich-elements/Grid";
import { gridItemComponent } from "./rich-elements/GridItem";
import { textComponent } from "./rich-elements/Text";
import { titleComponent } from "./rich-elements/Title";
import { captionComponent } from "./rich-elements/Caption";
import { dividerComponent } from "./rich-elements/Divider";
import { badgeComponent } from "./rich-elements/Badge";
import { iconComponent } from "./rich-elements/Icon";
import { linkComponent } from "./rich-elements/Link";
import { chartComponent } from "./rich-elements/Chart";

// Only server-validated symbolic names select these statically imported
// host-owned components; model content is never an import path or component.
// Only symbolic server-validated tag names map to host-owned presentation.
const presentation = {
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
};

