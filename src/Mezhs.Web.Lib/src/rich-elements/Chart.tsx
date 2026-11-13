import type { RichProps } from "./Element";
import { structured } from "./Style";
import { RichChart } from "../RichChart";
export const chartComponent = (props: RichProps) => <RichChart content={structured(props, "content")} />;
