import { defineElement, type RichProps } from "./Element";
import { read } from "./Style";
export const dividerElement = defineElement("divider", ["color"],
  (props: RichProps) => <hr className={"rich-divider" + (read(props, "color") === "strong" ? " rich-divider-strong" : "")} />);
