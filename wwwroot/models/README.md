# Custom GLB model for auth scene

Place your custom model at:

- `wwwroot/models/auth-object.glb`

The auth page (`wwwroot/js/auth-visuals.js`) will load this file with `THREE.GLTFLoader`.
If the file is missing or fails to load, the page falls back to procedural geometry.
