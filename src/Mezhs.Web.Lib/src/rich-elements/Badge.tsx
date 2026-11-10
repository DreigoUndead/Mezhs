import { defineElement, type RichProps } from "./Element";
import { tone } from "./Style";
export const badgeElement = defineElement("badge", ["color"],
  ({ children, ...props }: RichProps) => <span className={"rich-badge" + tone(props)}>{children}</span>);
