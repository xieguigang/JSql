USE meta;
DESCRIBE annotation;
SELECT id, mz FROM annotation WHERE mz > 100;
SELECT id, name FROM annotation WHERE adducts = '[M+H]+';
