import { ModRegistrar } from "cs2/modding";
import { TllButton } from "tll-button";
import { TllPanel } from "tll-panel";

const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", TllButton);
  moduleRegistry.append("Game", TllPanel);
};

export default register;
