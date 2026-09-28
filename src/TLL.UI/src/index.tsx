import { ModRegistrar } from "cs2/modding";
import { ErrorBoundary } from "error-boundary";
import { Planner } from "planner";
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
  moduleRegistry.append("Game", () => (
    <ErrorBoundary>
      <Planner />
    </ErrorBoundary>
  ));
};

export default register;
