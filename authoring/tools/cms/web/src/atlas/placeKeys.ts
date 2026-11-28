// A place's anchor key as the export writes it (#11220 R11220-1): a further plan mouth in a landmark cell has its cell's x
// moved by a whole stride per index, so its id is its own (PlannedUnderground.ExtraCellX in the product). The physical
// cell, where its tile lies, is recovered here as the product recovers it (PlannedUnderground.CellOf).

/** The span of anchor cells one mouth index takes in a key (2^40, the product's ExtraKeyStride). */
export const EXTRA_PLACE_KEY_STRIDE = 2 ** 40;

/** A place key's physical anchor cell x and its index among the plan mouths in that cell (0 for the cell's own site). */
export function placeCellOf(keyX: number): { cellX: number; index: number } {
  const index = Math.floor((keyX + EXTRA_PLACE_KEY_STRIDE / 2) / EXTRA_PLACE_KEY_STRIDE);
  return { cellX: keyX - index * EXTRA_PLACE_KEY_STRIDE, index };
}
