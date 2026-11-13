import type { RichProps } from "./Element";
import { layout } from "./Style";
export const rowComponent = ({ children, ...props }: RichProps) => <div className="rich-row" style={layout(props)}>{children}</div>;
