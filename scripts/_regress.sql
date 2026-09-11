CREATE DATABASE meta;
USE meta;
CREATE TABLE `annotation` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `db_xref` int unsigned NOT NULL COMMENT 'usually be the cad registry molecule id',
  `name` varchar(512) NOT NULL COMMENT 'name of the metabolite',
  `adducts` varchar(32) NOT NULL DEFAULT '[M+H]+' COMMENT 'precursor adducts ion type',
  `mz` double unsigned NOT NULL COMMENT 'precursor parent ion m/z value',
  `add_time` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `id_UNIQUE` (`id`),
  KEY `sort_ion` (`mz`)
) ENGINE=InnoDB AUTO_INCREMENT=833918 DEFAULT CHARSET=utf8mb3 COMMENT='metabolite ion information';
DESCRIBE annotation;
INSERT INTO annotation (id, db_xref, name, adducts, mz) VALUES (1, 100, 'glucose', '[M+H]+', 180.0634);
INSERT INTO annotation (id, db_xref, name, mz) VALUES (2, 101, 'lactate', 90.0317);
INSERT INTO annotation (id, db_xref, name, adducts, mz) VALUES (3, 102, 'citrate', '[M-H]-', 191.0197);
SELECT * FROM annotation;
SELECT name, mz FROM annotation WHERE mz > 100 ORDER BY mz DESC;
UPDATE annotation SET mz = 181.0 WHERE name = 'glucose';
SELECT name, mz FROM annotation WHERE name = 'glucose';
DELETE FROM annotation WHERE name = 'lactate';
SELECT COUNT(*) AS total, SUM(mz) AS sum_mz, AVG(mz) AS avg_mz, MIN(mz) AS min_mz, MAX(mz) AS max_mz FROM annotation;
SELECT adducts, COUNT(*) AS cnt FROM annotation GROUP BY adducts HAVING cnt >= 1 ORDER BY cnt DESC;
SELECT DISTINCT adducts FROM annotation;
SELECT name FROM annotation WHERE name LIKE 'gl%';
SELECT name FROM annotation WHERE mz BETWEEN 100 AND 200;
SELECT name FROM annotation WHERE name IN ('glucose', 'citrate') AND mz > 150;
CREATE INDEX idx_mz ON annotation (mz) USING BTREE;
CREATE INDEX idx_name ON annotation (name) USING HASH;
SELECT name, mz FROM annotation WHERE mz > 150;
SELECT name, mz FROM annotation WHERE mz >= 180 AND mz <= 200;
SELECT name FROM annotation WHERE name = 'citrate';
SELECT name, mz FROM annotation WHERE name = 'citrate' AND mz = 191.0197;
SHOW TABLES;
SHOW INDEXES FROM annotation;
SHOW STORAGE;
CHECKPOINT;
SELECT name, mz FROM annotation WHERE mz > 150;
