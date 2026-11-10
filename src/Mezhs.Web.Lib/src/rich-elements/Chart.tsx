import { defineElement, type RichProps } from "./Element";
import { structured } from "./Style";
import { RichChart } from "../RichChart";
export const chartElement = defineElement("Chart", ["content"],
  (props: RichProps) => <RichChart content={structured(props, "content")} />);
