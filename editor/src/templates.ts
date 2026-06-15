// テンプレート定義 — スタート画面から「ひな型を選んで始める」機能で使う
// buildJson の戻り値は loadFromJsonText(JSON.stringify(json)) に渡せる ekmap JSON オブジェクト

import { docToJsonAny } from "./model";
import { createBackroomsDoc } from "./presets";

export interface EkmTemplate {
    id: string;
    name: string;       // 表示名
    emoji: string;
    description: string; // 1行説明
    buildJson(name: string, author: string): unknown; // ekmap JSON オブジェクトを返す
}

// ---------- サンプル v1 データ (loadFromJsonText が v1→v3 変換を担う) ----------

const SAMPLE_ROOMS_BASE = {
    ekm: 1,
    name: "サンプルの部屋",
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

const GREAT_HALL_BASE = {
    ekm: 1,
    name: "大広間",
    author: "End K not",
    width: 24,
    height: 16,
    cells: [
        "########################",
        "#......................#",
        "#......................#",
        "#...#....#....#....#...#",
        "#......................#",
        "#......................#",
        "#...#....#....#....#...#",
        "#......................#",
        "#......................#",
        "#...#....#....#....#...#",
        "#......................#",
        "#......................#",
        "#...#....#....#....#...#",
        "#......................#",
        "#......................#",
        "########################",
    ],
    decor: [
        { kind: "light", x: 6, y: 1 },
        { kind: "light", x: 11, y: 1 },
        { kind: "light", x: 17, y: 1 },
        { kind: "light", x: 6, y: 14 },
        { kind: "light", x: 17, y: 14 },
    ],
    spawn: { x: 11, y: 8 },
    ambient: { visionRadius: 9 },
};

// ---------- テンプレート定義 ----------

export const EKM_TEMPLATES: EkmTemplate[] = [
    {
        id: "empty-backrooms",
        emoji: "\u{1F7EB}", // 🟫
        name: "空の Backrooms",
        description: "床・壁・奈落がすぐ使える 32×32 の白紙",
        buildJson(name: string, author: string): unknown {
            return docToJsonAny(createBackroomsDoc(32, 32, name, author));
        },
    },
    {
        id: "sample-rooms",
        emoji: "\u{1F6AA}", // 🚪
        name: "サンプルの部屋",
        description: "小部屋がつながった見本マップ",
        buildJson(name: string, author: string): unknown {
            const clone = structuredClone(SAMPLE_ROOMS_BASE) as Record<string, unknown>;
            clone.name = name;
            clone.author = author;
            return clone;
        },
    },
    {
        id: "great-hall",
        emoji: "\u{1F3DB}", // 🏛
        name: "大広間",
        description: "柱の並ぶ広い部屋",
        buildJson(name: string, author: string): unknown {
            const clone = structuredClone(GREAT_HALL_BASE) as Record<string, unknown>;
            clone.name = name;
            clone.author = author;
            return clone;
        },
    },
];
