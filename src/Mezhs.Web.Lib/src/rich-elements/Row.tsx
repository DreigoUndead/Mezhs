import { defineElement, type RichProps } from "./Element";
import { layout } from "./Style";
export const rowElement = defineElement("row", ["gap","align","justify","key"],
  ({ children, ...props }: RichProps) => <div className="rich-row" style={layout(props)}>{children}</div>);
