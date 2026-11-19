import type { RichProps } from "./Element";
import { read, surface } from "./Style";
export const boxComponent = ({ children, ...props }: RichProps) => <div className={"rich-box" + ((read(props, "border") === "true" || read(props, "border").startsWith("json:")) ? " rich-bordered" : "")} style={surface(props)}>{children}</div>;
