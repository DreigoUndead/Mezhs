import { createContext } from "react";

// Authority to send is supplied only by the owning chat host.
export const RichActionContext = createContext<((content: string) => void) | undefined>(undefined);
