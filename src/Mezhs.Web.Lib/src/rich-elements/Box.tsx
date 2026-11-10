import { defineElement, type RichProps } from "./Element";
import { read, surface } from "./Style";
export const boxElement = defineElement("box", ["gap","align","justify","padding","border","radius","background","width","flex","key"],
  ({ children, ...props }: RichProps) => <div className={"rich-box" + (read(props, "border") === "true" ? " rich-bordered" : "")} style={surface(props)}>{children}</div>);
