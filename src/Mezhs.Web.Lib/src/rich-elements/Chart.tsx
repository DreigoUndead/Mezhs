import type { RichProps } from "./Element";
import { structured } from "./Style";
import { RichChart } from "./ChartView";
export const chartComponent = (props: RichProps) => <RichChart content={structured(props, "content")} />;
