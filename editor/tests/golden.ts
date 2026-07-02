// 仕様 §10 golden サンプル (sample.ekmap.json) — 仕様書から逐語コピー。改変禁止。

export const golden = {
    ekm: 1,
    name: "Sample Rooms",
    author: "End K not",
    width: 16,
    height: 16,
    cells: [
        "################",
        "#......#.......#",
        "#......#...#...#",
        "#..##..#...#...#",
        "#......#...#...#",
        "#......#...##..#",
        "###.#######....#",
        "#..............#",
        "#....#....#....#",
        "#....#....#....#",
        "##.###....###.##",
        "#......##......#",
        "#......##......#",
        "#..#........#..#",
        "#..............#",
        "################",
    ],
    decor: [
        { kind: "light", x: 3, y: 2 },
        { kind: "light", x: 9, y: 3 },
        { kind: "light", x: 7, y: 7 },
        { kind: "light", x: 4, y: 12 },
        { kind: "light", x: 11, y: 12 },
        { kind: "stain", x: 2, y: 8 },
        { kind: "stain", x: 13, y: 5 },
        { kind: "vent", x: 14, y: 14 },
    ],
    spawn: { x: 7, y: 7 },
    ambient: { visionRadius: 8 },
};

// 型を緩めた clone (テストで自由に改変するため)
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function goldenClone(): any {
    return structuredClone(golden);
}
