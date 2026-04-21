import {
    BufferAttribute,
    TrianglesDrawMode,
    TriangleFanDrawMode,
    TriangleStripDrawMode
} from './three.module.js';

export function toTrianglesDrawMode(geometry, drawMode) {
    if (drawMode === TrianglesDrawMode) {
        return geometry;
    }

    if (drawMode !== TriangleFanDrawMode && drawMode !== TriangleStripDrawMode) {
        console.error('BufferGeometryUtils.toTrianglesDrawMode(): Unsupported draw mode:', drawMode);
        return geometry;
    }

    const index = geometry.getIndex();
    const position = geometry.getAttribute('position');

    if (!index && !position) {
        return geometry;
    }

    const numberOfTriangles = index ? index.count - 2 : position.count - 2;
    const newIndices = [];

    if (drawMode === TriangleFanDrawMode) {
        for (let i = 1; i <= numberOfTriangles; i++) {
            newIndices.push(index ? index.getX(0) : 0);
            newIndices.push(index ? index.getX(i) : i);
            newIndices.push(index ? index.getX(i + 1) : i + 1);
        }
    } else {
        for (let i = 0; i < numberOfTriangles; i++) {
            if (i % 2 === 0) {
                newIndices.push(index ? index.getX(i) : i);
                newIndices.push(index ? index.getX(i + 1) : i + 1);
                newIndices.push(index ? index.getX(i + 2) : i + 2);
            } else {
                newIndices.push(index ? index.getX(i + 2) : i + 2);
                newIndices.push(index ? index.getX(i + 1) : i + 1);
                newIndices.push(index ? index.getX(i) : i);
            }
        }
    }

    const newGeometry = geometry.clone();
    newGeometry.setIndex(new BufferAttribute(new Uint32Array(newIndices), 1));
    newGeometry.clearGroups();
    return newGeometry;
}
