# autoscan.py

# location and endpoint data has already been validated
# all that needs to be done is to connect to DB and update
# consider how I would add the functionality of multiple NIDs and locations. Eg from a file as well as just command line args
# for now just start with command line args

# add option to specify a file 
import argparse
import sys

import sqlite3
import subprocess

from datetime import datetime
import re

#leading characters of a barcode sometimes indicate device type
product_type_prefixes = {
    'T1' : ('TC-1116', '16'),
    'T2' : ('TC-1216', '3'),
    'T3' : ('TC-1120', '39'),
    'T4' : ('TC-1120-RD', '40'),
    'T5' : ('TC-1220', '41'),
    'T6' : ('TC-1220-RD', '42'),
    'TQ' : ('PP-1316', '12'),
    'X1' : ('XR-3100', '14'),
    'TD' : ('ERT', '') #<<this one may or may not be a thing
}

#attempts to connect to database
def setup_db():
    try:
    # 	#conn = sqlite3.connect('/var/www/inventory.db')
        conn = sqlite3.connect('/home/jmorrison/inventory-UI-and-scripts/inventory_tester.db')
        # cursor = conn.cursor()
        # cursor.execute("SELECT name from sqlite_master WHERE type='table';")
        # print(cursor.fetchall())
        print("Connected to database")
    except sqlite3.Error as e:
        print("Error connecting to database: {}".format(e))
        exit(400)

    return conn

def commit(db):
    try:
        db.commit()
        print('commit')
    except sqlite3.Error as e:
        print("Error committing to database: {}".format(e))
        exit(2)

#takes a socket barcode and parses it into a location, socket form and voltage
def parse_socket(barcode):
    
    location_data = {
        'location'			: '',
        'location_prefix' 	: '',
        'form'				: '',
        'voltage'			: '',
        'read_date'			: datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    }
    
    pillar_format = r'P[NESW][\d]{4}'
    lab_format = r'Lab Stock Area'

    print(re.match(pillar_format, barcode))
    #if on a pillar - do I need others?
    if re.match(pillar_format, barcode):
        location_data['location'] = "{}{}{}".format(barcode[2:4],barcode[1],barcode[4:6])
        location_data['location_prefix'] = "P-"
        location_data['form'] = barcode[6:-3]
        location_data['voltage'] = barcode [-3:]

    if re.match(lab_format, barcode):
        location_data['location'] = "xxxx"
        location_data['location_prefix'] = "L-"

    return location_data

def wipe_socket(db, location_data):
    ## should there be different behaviour for different locations?? EG programming lab

    #when a new location is scanned, set all devices previously at that location to location = None
    db.cursor().execute("UPDATE endpoints SET location='' WHERE location_prefix = ? AND location = ?", (location_data['location_prefix'], location_data['location']))

    commit(db)

def remove_endpoint(db, nid):
    db.cursor().execute("UPDATE endpoints SET location='' WHERE network_id=?", (nid,))
    commit(db)

def check_endpoint(db, nid, remove=False):

    #check if there are aleady instances of this nid in the db
    query_result = db.cursor().execute("SELECT user, comment FROM endpoints WHERE network_id=?", (nid,)).fetchone()  # input needs to be tuple 

    print(query_result)

    # if there is a comment and/or user attached to the device return error code 3 if trying to remove
    if query_result and remove:
        if (query_result[0] != None or query_result[1] != None):
            print("Here I am")
            exit(3)
    # if not trying to remove, just return insert or update
    else:
        return (query_result == None)

    return False

#adds endpoint network ID and socket data to the database
def add_endpoint(db, data, nid, nid_prefix, insert):

    #some barcodes have leading characters indicating product type/id, check for those
    product_name, product_id = product_type_prefixes.get(nid_prefix,('',''))
    
    query_variables = (data['location_prefix'], data['location'], data['read_date'], data['form'], data['voltage'], product_name, product_id, nid)
    
    #if the nid exists, update it, if not, insert it
    if not insert:
        query = "UPDATE endpoints SET location_prefix = ?, location = ?, read_date = ?, socket_form = ?, voltage = ?, product_name = ?, product_id = ? WHERE network_id = ?"
    else:
        query = "INSERT INTO endpoints (location_prefix, location, read_date, socket_form, voltage, product_name, product_id, network_id) VALUES (?,?,?,?,?,?,?,?)"
    
    print(query)
    # #run query
    db.cursor().execute(query, query_variables)
    	
    commit(db)

if __name__=="__main__":
    # establish db connection
    db = setup_db()

    endpoint = sys.argv[1]
    #chop the barcode so it's just the nid
    nid = endpoint[-10:]
    nid_prefix = endpoint[:2]

    # if removing won't have location parameter
    try:
        location = sys.argv[2]
        # # parse location into dictionary
        location_data = parse_socket(location)
        # if location holds multiple units, do not wipe socket
        if (location_data['location_prefix'] == "P-"):
            # # wipe any entries for that socket
            wipe_socket(db, location_data)
            
        insert = check_endpoint(db, nid)
        add_endpoint(db, location_data, nid, nid_prefix, insert)

    except IndexError:
        check_endpoint(db, nid, remove=True)
        remove_endpoint(db, nid)
