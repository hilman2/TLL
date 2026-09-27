import { ModRegistrar } from "cs2/modding";
import { ErrorBoundary } from "error-boundary";
import { TllButton } from "tll-button";
import { TllPanel } from "tll-panel";

const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", () => (
    <ErrorBoundary>
      <TllButton />
    </ErrorBoundary>
  ));
  moduleRegistry.append("Game", () => (
    <ErrorBoundary>
      <TllPanel />
    </ErrorBoundary>
  ));
};

export default register;
