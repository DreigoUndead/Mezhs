import type { RichProps } from "./Element";
import { read, surface } from "./Style";
export const boxComponent = ({ children, ...props }: RichProps) => <div className={"rich-box" + (read(props, "border") === "true" ? " rich-bordered" : "")} style={surface(props)}>{children}</div>;
