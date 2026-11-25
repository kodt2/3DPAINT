import { describe, expect, it } from 'vitest';
import { DECODER_THREE_VERSION } from '../../generated/decoder-version';
import { DECODERS_BASE_URL, isDecoderAssetUrl } from './decoder-assets';

describe('decoder assets', () => {
  it('builds the versioned base URL from the three version', () => {
    expect(DECODERS_BASE_URL).toBe(`/assets/decoders/three-${DECODER_THREE_VERSION}/`);
    expect(DECODER_THREE_VERSION).toMatch(/^\d+\.\d+\.\d+$/);
  });

  it('trusts decoder asset URLs under the base', () => {
    expect(isDecoderAssetUrl(`${DECODERS_BASE_URL}draco/draco_decoder.wasm`)).toBe(true);
    expect(isDecoderAssetUrl(`${DECODERS_BASE_URL}basis/basis_transcoder.js`)).toBe(true);
  });

  it('does not trust other paths or other three versions', () => {
    expect(isDecoderAssetUrl(`${DECODERS_BASE_URL}`)).toBe(false);
    expect(isDecoderAssetUrl(`/assets/decoders/three-0.0.0/draco/draco_decoder.wasm`)).toBe(false);
    expect(isDecoderAssetUrl('/assets/other/wasm.js')).toBe(false);
    // A remote URL that merely contains the prefix is not a same-origin path.
    expect(isDecoderAssetUrl(`https://cdn.example.com${DECODERS_BASE_URL}draco/x.js`)).toBe(false);
  });
});
