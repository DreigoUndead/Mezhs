import type { RichProps } from "./Element";
import { layout } from "./Style";
export const colComponent = ({ children, ...props }: RichProps) => <div className="rich-col" style={layout(props)}>{children}</div>;
