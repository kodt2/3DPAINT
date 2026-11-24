/**
 * WebGL availability probe (step 3c.3). The viewer needs WebGL 1 or 2;
 * when neither context can be created the page shows a friendly message
 * instead of a blank canvas.
 */
export function isWebGLAvailable(): boolean {
  try {
    const canvas = document.createElement('canvas');
    return Boolean(canvas.getContext('webgl2') ?? canvas.getContext('webgl'));
  } catch {
    return false;
  }
}
