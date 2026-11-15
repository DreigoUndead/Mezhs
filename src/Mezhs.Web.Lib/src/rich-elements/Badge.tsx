import type { RichProps } from "./Element";
import { tone } from "./Style";
export const badgeComponent = ({ children, ...props }: RichProps) => <span className={"rich-badge" + tone(props)}>{children}</span>;
