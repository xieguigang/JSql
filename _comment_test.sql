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
  UNIQUE KEY `find_ion_by_id` (`db_xref`,`adducts`),
  KEY `data_group` (`db_xref`,`name`),
  KEY `adducts_index` (`adducts`),
  KEY `sort_ion` (`mz`)
) ENGINE=InnoDB AUTO_INCREMENT=833918 DEFAULT CHARSET=utf8mb3 COMMENT='metabolite ion information';
DESCRIBE annotation;
INSERT INTO annotation (id, db_xref, name, adducts, mz) VALUES (1, 100, 'glucose', '[M+H]+', 180.0634);
INSERT INTO annotation (id, db_xref, name, mz) VALUES (2, 101, 'lactate', 90.0317);
SELECT * FROM annotation;
SELECT id, name FROM annotation WHERE adducts = '[M+H]+';
CREATE INDEX sort_ion ON annotation (mz);
SELECT id, mz FROM annotation WHERE mz > 100;
