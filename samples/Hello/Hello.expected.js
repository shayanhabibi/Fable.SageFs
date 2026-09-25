import { Union } from "./fable_modules/fable-library-js/Types.js";
import { union_type, float64_type } from "./fable_modules/fable-library-js/Reflection.js";
import { concat } from "./fable_modules/fable-library-js/String.js";
import { ofArray, sumBy } from "./fable_modules/fable-library-js/List.js";

export class Shape extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["Circle", "Rect"];
    }
}

export function Shape_$reflection() {
    return union_type("Hello.Shape", [], Shape, () => [[["radius", float64_type]], [["width", float64_type], ["height", float64_type]]]);
}

export function area(shape) {
    if (shape.tag === 1) {
        const w = shape.fields[0];
        const h = shape.fields[1];
        return w * h;
    }
    else {
        const r = shape.fields[0];
        return (3.141592653589793 * r) * r;
    }
}

export function greet(name) {
    return concat("Hello, ", name, "!");
}

export const total = sumBy(area, ofArray([new Shape(/* Circle */ 0, [1]), new Shape(/* Rect */ 1, [2, 3])]), {
    GetZero: () => 0,
    Add: (x, y) => (x + y),
});
