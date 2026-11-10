import { defineElement, type RichProps } from "./Element";
import { layout } from "./Style";
export const colElement = defineElement("col", ["gap","align","justify","key","flex"],
  ({ children, ...props }: RichProps) => <div className="rich-col" style={layout(props)}>{children}</div>);
