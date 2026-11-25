/**
 * Type declaration for the untyped draco3dgltf package (Node-only Draco
 * encoder/decoder, test fixture generation, step 3c.5).
 */
declare module 'draco3dgltf' {
  // The concrete module shape is opaque to the viewer; gltf-transform only
  // needs the promise result of createEncoderModule()/createDecoderModule().
  const draco3dgltf: {
    createDecoderModule(config?: Record<string, unknown>): Promise<unknown>;
    createEncoderModule(config?: Record<string, unknown>): Promise<unknown>;
  };
  export default draco3dgltf;
}
