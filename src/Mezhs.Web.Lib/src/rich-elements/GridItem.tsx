import { defineElement, type RichProps } from "./Element";
export const gridItemElement = defineElement("grid-item", ["key"],
  ({ children }: RichProps) => <div className="rich-grid-item">{children}</div>);
